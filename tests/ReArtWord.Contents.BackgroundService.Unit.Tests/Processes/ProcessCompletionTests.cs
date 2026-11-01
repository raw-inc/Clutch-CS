using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>終了コードだけでなく、所有ファイルを回収できることを実プロセスで確認します。</summary>
public sealed class ProcessCompletionTests
{
    /// <summary>通常終了と強制終了のどちらでも、終了確認後にはファイルを使用できます。</summary>
    /// <param name="kill">OSに強制終了を要求するかです。</param>
    /// <returns>終了シグナルとファイル解放の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedProcessHasReleasedItsFilesAsync(bool kill)
    {
        using MediaTestFiles files = new();
        string configuration = Path.Combine(files.Root, "process.txt");
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        using Process process = await StartAsync(configuration, limit.Token).ConfigureAwait(true);
        try
        {
            if (kill)
            {
                process.Kill();
            }
            else
            {
                await File.WriteAllTextAsync(configuration + ".release", string.Empty, limit.Token).ConfigureAwait(true);
            }
            await ProcessCompletion.WaitAsync(process, limit.Token).ConfigureAwait(true);
            Assert.True(ProcessCompletion.HasExited(process));
            using FileStream reopened = File.Open(configuration + ".0.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            await StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>待機の取消は対象プロセスを停止せず、停止の判断を所有元へ返します。</summary>
    /// <returns>取消と、その後の明示的な回収の確認です。</returns>
    // 抑制理由: finallyで待機Taskを回収してからProcessと取消元を破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "失敗時にもfinallyで所有する待機Taskの完了を待ちます。")]
    [Fact]
    public async Task CancelledWaitDoesNotStopTheProcessAsync()
    {
        using MediaTestFiles files = new();
        string configuration = Path.Combine(files.Root, "process.txt");
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        using Process process = await StartAsync(configuration, limit.Token).ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        Task? waiting = null;
        try
        {
            waiting = ProcessCompletion.WaitAsync(process, cancellation.Token);
            await cancellation.CancelAsync().ConfigureAwait(true);
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(limit.Token)).ConfigureAwait(true);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(ProcessCompletion.HasExited(process));
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            try
            {
                await StopAsync(process).ConfigureAwait(true);
            }
            finally
            {
                if (waiting is not null)
                {
                    _ = await Record.ExceptionAsync(() => waiting).ConfigureAwait(true);
                }
            }
        }
    }

    /// <summary>OSの終了確認後は、取り消されたトークンでも既存の終了結果を維持します。</summary>
    /// <returns>完了済みの待機契約の確認です。</returns>
    [Fact]
    public async Task CompletedProcessKeepsItsResultWithACancelledTokenAsync()
    {
        using MediaTestFiles files = new();
        string configuration = Path.Combine(files.Root, "process.txt");
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        using Process process = await StartAsync(configuration, limit.Token).ConfigureAwait(true);
        try
        {
            await File.WriteAllTextAsync(configuration + ".release", string.Empty, limit.Token).ConfigureAwait(true);
            await ProcessCompletion.WaitAsync(process, limit.Token).ConfigureAwait(true);
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync().ConfigureAwait(true);
            await ProcessCompletion.WaitAsync(process, cancelled.Token).ConfigureAwait(true);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            await StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>全ファイルを取得したことを確認してから、所有する試験プロセスを返します。</summary>
    /// <returns>呼出元が停止・破棄する実体です。</returns>
    // 抑制理由: 起動後の確認失敗でもプロセスを回収し、元の例外を維持します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "回収後に起動確認の失敗を保持します。")]
    private static async Task<Process> StartAsync(string configuration, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(configuration, "held-files", cancellationToken).ConfigureAwait(false);
        string build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string suffix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            suffix = ".exe";
        }
        string executable = Path.GetFullPath("../../../../../tests/ReArtWord.Contents.BackgroundService.Unit.Tests/CommandLine/FixtureHost/bin/" + build
                                            + "/net10.0/ContentsCommandLineFixture" + suffix, AppContext.BaseDirectory);
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configuration);
        Process process = Process.Start(start) ?? throw new IOException("The process fixture did not start.");
        try
        {
            string ready = configuration + ".ready";
            while (!File.Exists(ready))
            {
                Assert.False(ProcessCompletion.HasExited(process));
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
            return process;
        }
        catch
        {
            try
            {
                await StopAsync(process).ConfigureAwait(false);
            }
            finally
            {
                process.Dispose();
            }
            throw;
        }
    }

    /// <summary>所有する実体の終了を確認してから、一時配置の回収へ進みます。</summary>
    /// <returns>実プロセスの回収です。</returns>
    private static async Task StopAsync(Process process)
    {
        if (!ProcessCompletion.HasExited(process))
        {
            process.Kill();
        }
        await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
    }
}
