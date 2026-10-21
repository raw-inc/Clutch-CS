using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.CommandLine;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.CommandLine;

/// <summary>偽プロセス APIを使わず実子の起動・終了・取消を確認します。</summary>
public sealed class ServiceRunTests
{
    /// <summary>空白を含む設定パスを単一引数として渡し実終了コードを維持します。</summary>
    /// <returns>実終了確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task ForegroundWaitsForActualExitAsync(int expected)
    {
        using ServiceFixture fixture = new(expected.ToString(CultureInfo.InvariantCulture));
        int code = await fixture.RunAsync().ConfigureAwait(true);
        Assert.Equal(expected, code);
        Assert.True(File.Exists(fixture.Config + ".started"));
        JsonElement[] results = fixture.Results();
        Assert.Equal("started", results[0].GetProperty("state").GetString());
        Assert.Equal("exited", results[1].GetProperty("state").GetString());
        Assert.Equal(expected, results[1].GetProperty("childExitCode").GetInt32());
        Assert.Equal(results[0].GetProperty("launchId").GetGuid(), results[1].GetProperty("launchId").GetGuid());
        Assert.NotEqual(Environment.ProcessId, results[0].GetProperty("processId").GetInt32());
    }

    /// <summary>取消後に通常終了の猶予を与え、必要時だけ強制終了して実終了を確認します。</summary>
    /// <returns>生存子が残らない確認です。</returns>
    [Theory]
    [InlineData("wait", true)]
    [InlineData("grace", false)]
    public async Task CancellationWaitsForChildBeforeReturningAsync(string behavior, bool forced)
    {
        using ServiceFixture fixture = new(behavior);
        using CancellationTokenSource cancellation = new();
        Task<int> running = fixture.RunAsync(cancellationToken: cancellation.Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string startedPath = fixture.Config + ".started";
        while (!File.Exists(startedPath))
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        await cancellation.CancelAsync().ConfigureAwait(true);
        Assert.Equal(130, await running.ConfigureAwait(true));
        JsonElement final = fixture.Results().Last();
        Assert.Equal("exited", final.GetProperty("state").GetString());
        Assert.True(final.GetProperty("cancelled").GetBoolean());
        Assert.Equal(forced, final.GetProperty("forcedTerminationRequested").GetBoolean());
        int id = final.GetProperty("processId").GetInt32();
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(id));
    }

    /// <summary>未開始・OS起動失敗・開始前取消を成功へ変えません。</summary>
    /// <returns>副作用を開始しない入力境界です。</returns>
    [Fact]
    public async Task InvalidAndCancelledLaunchNeverStartsAsync()
    {
        using ServiceFixture fixture = new("0");
        Assert.Equal(2, await fixture.RunAsync(executable: fixture.Config + ".missing").ConfigureAwait(true));
        Assert.Empty(fixture.Results());
        Assert.Equal(7, await fixture.RunAsync(executable: fixture.Config).ConfigureAwait(true));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync().ConfigureAwait(true);
        Assert.Equal(130, await fixture.RunAsync(cancellationToken: cancelled.Token).ConfigureAwait(true));
        Assert.Equal("notStarted", fixture.Results().Last().GetProperty("state").GetString());
        Assert.False(File.Exists(fixture.Config + ".started"));
    }

    /// <summary>実試験用の構成と一時入力だけを所有します。</summary>
    private sealed class ServiceFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "contents-cli-" + Guid.NewGuid().ToString("N"));
        private readonly StringWriter output = new(CultureInfo.InvariantCulture);
        private readonly StringWriter error = new(CultureInfo.InvariantCulture);

        /// <summary>任意動作をテスト用設定へ保存します。</summary>
        internal ServiceFixture(string behavior)
        {
            Directory.CreateDirectory(directory);
            Config = Path.Combine(directory, "contents test.toml");
            File.WriteAllText(Config, behavior);
        }

        internal string Config
        {
            get;
        }

        /// <summary>製品CLI入口からURLなしで子を起動します。</summary>
        /// <returns>CLIの実終了コードです。</returns>
        internal async Task<int> RunAsync(string? executable = null, CancellationToken cancellationToken = default)
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            string filename = OperatingSystem.IsWindows() ? "ContentsCommandLineFixture.exe"
                                                          : "ContentsCommandLineFixture";
            string path = executable ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../CommandLine/FixtureHost/bin", configuration, "net10.0", filename));
            using StringReader input = new(string.Empty);
            return await CommandLineApplication.RunAsync(["service", "run", "--executable", path, "--config", Config], input, output, error, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>JSON行をコピーしてプロセス所有結果を返します。</summary>
        /// <returns>確認済みイベントです。</returns>
        internal JsonElement[] Results() => output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();

        /// <summary>終了した子の一時入力だけを解放します。</summary>
        public void Dispose()
        {
            output.Dispose();
            error.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
