using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実プロセスの標準pipeで文字境界、長い出力、無出力時の取消を確認します。</summary>
public sealed class BoundedProcessRunnerTests
{
    /// <summary>実停止の確認10秒とパイプの排出2秒を含む、取消完了の待機上限です。</summary>
    private static TimeSpan CancellationCompletionLimit => TimeSpan.FromSeconds(15);

    /// <summary>診断追加前にコンパイルした呼出しが使う、七引数の構築と分解代入を維持します。</summary>
    [Fact]
    public void DiagnosticsPreserveCommandLimitsBinaryContract()
    {
        Type[] parameters = [typeof(int), typeof(bool), typeof(long?), typeof(string), typeof(string), typeof(System.Text.Encoding), typeof(IReadOnlyDictionary<string, string>)];
        System.Reflection.ConstructorInfo? constructor = typeof(ProcessRunLimits).GetConstructor(parameters);
        Assert.NotNull(constructor);
        ProcessRunLimits limits = Assert.IsType<ProcessRunLimits>(constructor.Invoke([12345, true, null, null, null, null, null]));
        System.Reflection.MethodInfo? deconstruct = typeof(ProcessRunLimits).GetMethod("Deconstruct", parameters.Select(parameter => parameter.MakeByRefType()).ToArray());
        Assert.NotNull(deconstruct);
        object?[] values = new object?[parameters.Length];
        deconstruct.Invoke(limits, values);
        Assert.Equal(12345, values[0]);
        Assert.True(Assert.IsType<bool>(values[1]));
        Assert.Null(limits.DiagnosticOutput);
    }

    /// <summary>本体の正常終了後に子孫がpipeを保持していても、受信済みの出力を失いません。</summary>
    /// <returns>本体の終了と、pipeの読取り期限を別々に扱う確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InheritedPipesPreserveOutputAfterProcessExit(bool empty, bool failed)
    {
        using MediaTestFiles files = new();
        string config = Path.Combine(files.Root, "inherited.txt");
        string mode = "inherited-output";
        string expected = "completed";
        if (empty)
        {
            mode = "inherited-empty";
            expected = string.Empty;
        }
        if (failed)
        {
            mode = "inherited-failure";
        }
        await File.WriteAllTextAsync(config, mode).ConfigureAwait(true);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        List<string> diagnostics = [];
        Task<string> running = BoundedProcessRunner.RunAsync(Executable(), ["--config", config], new ProcessRunLimits { DiagnosticOutput = diagnostics.Add }, cancellation.Token);
        Process? child = null;
        try
        {
            int pid = await ReadStartedProcessAsync(config + ".child", cancellation.Token).ConfigureAwait(true);
            child = Process.GetProcessById(pid);
            if (failed)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => running).ConfigureAwait(true);
            }
            else
            {
                Assert.Equal(expected, await running.ConfigureAwait(true));
            }
            Assert.False(child.HasExited);
            Assert.False(cancellation.IsCancellationRequested);
            string report = Assert.Single(diagnostics);
            Assert.Contains("pipeDrainTimedOut=True", report, StringComparison.Ordinal);
            Assert.Contains(failed ? "exitCode=7"
                                   : "exitCode=0", report, StringComparison.Ordinal);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            if (child is not null)
            {
                using (child)
                {
                    if (!child.HasExited)
                    {
                        child.Kill(true);
                    }
                    await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
                }
            }
        }
    }

    /// <summary>成功・失敗の終了コードと有限の標準エラーを、結果を変えずに記録します。</summary>
    /// <returns>実プロセスの診断内容と標準出力の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsPreserveResultAndBoundStandardErrorAsync(bool failed)
    {
        using MediaTestFiles files = new();
        string config = Path.Combine(files.Root, "diagnostics.txt");
        await File.WriteAllTextAsync(config, failed ? "diagnostic-failure"
                                                    : "diagnostic-success").ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        List<string> diagnostics = [];
        Task<string> running = BoundedProcessRunner.RunAsync(Executable(), ["--config", config], new ProcessRunLimits { DiagnosticOutput = diagnostics.Add }, timeout.Token);
        if (failed)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => running).ConfigureAwait(true);
        }
        else
        {
            Assert.Equal("completed", await running.ConfigureAwait(true));
        }
        string report = Assert.Single(diagnostics);
        Assert.Contains(failed ? "exitCode=7"
                               : "exitCode=0", report, StringComparison.Ordinal);
        Assert.Contains("stderrTruncated=True", report, StringComparison.Ordinal);
        Assert.Contains("known-error", report, StringComparison.Ordinal);
        Assert.DoesNotContain("end-of-stderr", report, StringComparison.Ordinal);
        Assert.Contains("pipeDrainTimedOut=False", report, StringComparison.Ordinal);
        if (failed)
        {
            Assert.Contains(nameof(InvalidDataException), report, StringComparison.Ordinal);
        }
    }

    /// <summary>診断先の例外で、本来の正常終了や異常終了を置き換えません。</summary>
    /// <returns>診断先が失敗する場合の実終了結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticSinkFailureDoesNotChangeResultAsync(bool failed)
    {
        using MediaTestFiles files = new();
        string config = Path.Combine(files.Root, "diagnostics.txt");
        await File.WriteAllTextAsync(config, failed ? "7"
                                                    : "0").ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<string> running = BoundedProcessRunner.RunAsync(Executable(), ["--config", config], new ProcessRunLimits { DiagnosticOutput = _ => throw new InvalidOperationException("diagnostic sink failed") }, timeout.Token);
        if (failed)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => running).ConfigureAwait(true);
        }
        else
        {
            Assert.Equal(string.Empty, await running.ConfigureAwait(true));
        }
    }

    /// <summary>起動前の失敗は、終了コードを捏造せず元の例外とともに記録します。</summary>
    /// <returns>プロセスが存在しない失敗の診断です。</returns>
    [Fact]
    public async Task DiagnosticReportsStartupFailureAsync()
    {
        using MediaTestFiles files = new();
        List<string> diagnostics = [];
        await Assert.ThrowsAsync<FileNotFoundException>(() => BoundedProcessRunner.RunAsync(Path.Combine(files.Root, "missing.exe"), [], new ProcessRunLimits { DiagnosticOutput = diagnostics.Add })).ConfigureAwait(true);
        string report = Assert.Single(diagnostics);
        Assert.Contains("exitCode=unavailable", report, StringComparison.Ordinal);
        Assert.Contains(nameof(FileNotFoundException), report, StringComparison.Ordinal);
    }

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
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        string result = await BoundedProcessRunner.RunAsync(Executable(), ["--config", config], new ProcessRunLimits(StandardInput: input, OutputEncoding: System.Text.Encoding.UTF8), timeout.Token);
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
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string actual = await BoundedProcessRunner.RunAsync(Executable(), ["--config", config], timeout.Token);
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
        List<string> diagnostics = [];
        Task<string> running = BoundedProcessRunner.RunAsync(Executable(), ["--config", config], new ProcessRunLimits { DiagnosticOutput = diagnostics.Add }, cancellation.Token);
        Process? child = null;
        try
        {
            int pid = await ReadStartedProcessAsync(config, cancellation.Token).ConfigureAwait(true);
            child = Process.GetProcessById(pid);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(CancellationCompletionLimit).ConfigureAwait(false));
            Assert.Contains("CanceledException", Assert.Single(diagnostics), StringComparison.Ordinal);
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
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.WaitAsync(CancellationCompletionLimit).ConfigureAwait(false));
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
                string value = await PublishedFixtureFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
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
