using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実プロセスの標準pipeで文字境界、長い出力、無出力時の取消を確認します。</summary>
public sealed class BoundedProcessRunnerTests
{
    /// <summary>標準入出力の容量を超えるJSONも、引数へ展開せずUTF-8とEOFで渡します。</summary>
    /// <returns>同時出力中の全文受渡しと空入力の終了確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardInputPreservesUnicodeAndSignalsEndAsync(bool empty)
    {
        using MediaTestFiles files = new();
        string config = Path.Combine(files.Root, "input.txt");
        await File.WriteAllTextAsync(config, "input");
        string input = empty ? string.Empty
                             : string.Concat(Enumerable.Repeat("日本語🚀\"\\\n", 16384));
        string result = await BoundedProcessRunner.RunAsync(Executable(), ["--config", config], new ProcessRunLimits(StandardInput: input, OutputEncoding: System.Text.Encoding.UTF8)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(new string('o', 8192) + input, result);
    }

    /// <summary>BOMと複数byte文字が分割されても、先読み分を残さず全出力を返します。</summary>
    /// <returns>pipe容量を超えるUTF-8出力の内容確認です。</returns>
    [Fact]
    public async Task ChunkedUtf8OutputPreservesBomAndCharacters()
    {
        using MediaTestFiles files = new();
        string config = Path.Combine(files.Root, "behavior.txt");
        await File.WriteAllTextAsync(config, "pipe");
        string actual = await BoundedProcessRunner.RunAsync(Executable(), ["--config", config]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("日本語🚀" + new string('x', 8192), actual);
    }

    /// <summary>標準出力・標準エラーの両方が無出力でも、取消後に実プロセスとreaderを終了します。</summary>
    /// <returns>実停止と有限時間内の取消通知です。</returns>
    [Fact]
    public async Task CancellationClosesQuietProcessAndBothPipes()
    {
        using MediaTestFiles files = new();
        string config = Path.Combine(files.Root, "behavior.txt");
        await File.WriteAllTextAsync(config, "wait");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task<string> running = BoundedProcessRunner.RunAsync(Executable(), ["--config", config], cancellation.Token);
        Process? child = null;
        try
        {
            int pid = await ReadStartedProcessAsync(config, cancellation.Token).ConfigureAwait(true);
            child = Process.GetProcessById(pid);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            if (child is not null)
            {
                Assert.True(child.HasExited);
                child.Dispose();
            }
        }
    }

    /// <summary>起動待ちの直列化が実行時間へ広がらず、複数の無出力プロセスを同時に所有・取消できます。</summary>
    /// <returns>両方が実開始してから、両方の実停止を確認します。</returns>
    [Fact]
    public async Task QuietProcessesRunConcurrentlyAndBothStop()
    {
        using MediaTestFiles files = new();
        string[] configs = [Path.Combine(files.Root, "first.txt"), Path.Combine(files.Root, "second.txt")];
        foreach (string config in configs)
        {
            await File.WriteAllTextAsync(config, "wait");
        }
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task<string>[] running = configs.Select(config => BoundedProcessRunner.RunAsync(Executable(), ["--config", config], cancellation.Token)).ToArray();
        List<Process> children = [];
        try
        {
            foreach (string config in configs)
            {
                int pid = await ReadStartedProcessAsync(config, cancellation.Token).ConfigureAwait(true);
                children.Add(Process.GetProcessById(pid));
            }
            Assert.All(children, child => Assert.False(child.HasExited));
        }
        finally
        {
            await cancellation.CancelAsync();
            foreach (Task<string> task in running)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            }
            foreach (Process child in children)
            {
                Assert.True(child.HasExited);
                child.Dispose();
            }
        }
    }

    /// <summary>原子的なマーカー公開直後の共有違反だけを、元の期限内で待ち直します。</summary>
    /// <returns>子自身が公開した起動PIDです。</returns>
    private static async Task<int> ReadStartedProcessAsync(string config, CancellationToken cancellationToken = default)
    {
        string path = config + ".started";
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                string value = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                // renameや別processの一時handleだけを待ち、その他のIO失敗は隠しません。
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>同じ構成でビルドした隔離試験用apphostだけを起動します。</summary>
    /// <returns>試験用実行物の絶対パスです。</returns>
    private static string Executable()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string suffix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            suffix = ".exe";
        }
        return Path.GetFullPath("../../../../../tests/ReArtWord.Contents.BackgroundService.Unit.Tests/CommandLine/FixtureHost/bin/" + configuration
            + "/net10.0/ContentsCommandLineFixture" + suffix, AppContext.BaseDirectory);
    }
}
