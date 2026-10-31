using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>専用jobの照会だけを行う実Updaterを起動し、所有するプロセスを回収します。</summary>
internal static class SetupOperationProcess
{
    /// <summary>開始済みjobを別プロセスへ渡し、同じ操作の結果だけを取得します。</summary>
    /// <returns>Updaterが返した操作状態です。</returns>
    internal static async Task<SetupResult> ObserveAsync(string jobPath, CancellationToken cancellationToken = default)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        string name = "ReArtWord.Contents.BackgroundService.Updater";
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        string executable = Path.Combine(root, "src", "ReArtWord.Contents.BackgroundService.Updater", "bin", configuration, "net10.0", name);
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--setup-job");
        start.ArgumentList.Add(jobPath);
        using Process process = Process.Start(start) ?? throw new IOException("The test updater could not start.");
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(output, errors, ProcessCompletion.WaitAsync(process, cancellationToken)).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SetupResult>(await output.ConfigureAwait(false), ContractJson.Options)
                ?? throw new InvalidDataException(await errors.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
