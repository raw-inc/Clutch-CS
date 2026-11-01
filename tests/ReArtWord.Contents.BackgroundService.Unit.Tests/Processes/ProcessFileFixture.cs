using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実プロセスによるファイル保持と終了の境界を再現します。</summary>
internal static class ProcessFileFixture
{
    /// <summary>全ファイルを取得したことを確認してから、所有する試験プロセスを返します。</summary>
    /// <returns>呼出元が停止・破棄する実体です。</returns>
    // 抑制理由: 起動後の確認失敗でもプロセスを回収し、元の例外を維持します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "回収後に起動確認の失敗を保持します。")]
    internal static async Task<Process> StartAsync(string configuration, string? executablePath = null, CancellationToken cancellationToken = default)
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
        ProcessStartInfo start = new(executablePath ?? executable) { UseShellExecute = false, CreateNoWindow = true };
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
    internal static async Task StopAsync(Process process)
    {
        if (!ProcessCompletion.HasExited(process))
        {
            process.Kill();
        }
        await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
    }
}
