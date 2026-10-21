using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>明示したCore実行物を別プロセスとして前景で所有します。</summary>
internal static class ServiceRunner
{
    /// <summary>起動と実終了を待ち、取消でも子の生存を成功にしません。</summary>
    /// <returns>子終了コード、入力不正2、起動失敗7、終了不明5、取消後終了確認130です。</returns>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo start;
        try
        {
            start = Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("service input invalid; no child started; require service run --executable PATH --config TOML").ConfigureAwait(false);
            return 2;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { state = "notStarted", cancelled = true })).ConfigureAwait(false);
            return 130;
        }
        using Process child = new() { StartInfo = start };
        try
        {
            if (!child.Start())
            {
                await error.WriteLineAsync("service start failed; no child confirmed").ConfigureAwait(false);
                return 7;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            await error.WriteLineAsync("service start failed; no child confirmed").ConfigureAwait(false);
            return 7;
        }
        int processId = child.Id;
        Guid launchId = Guid.NewGuid();
        await output.WriteLineAsync(JsonSerializer.Serialize(new { launchId, processId, state = "started", readinessConfirmed = false })).ConfigureAwait(false);
        bool cancelled = false;
        bool forced = false;
        try
        {
            await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;

            // 同じconsoleのCtrl+Cを受けたGenericHostへ先に通常終了の猶予を与えます。
            if (!await WaitAsync(child, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false))
            {
                forced = true;
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // 終了競合または権限不足を実際の終了確認と分けます。
                }
                if (!await WaitAsync(child, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false))
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { launchId, processId, state = "unknown", cancelled, forcedTerminationRequested = forced })).ConfigureAwait(false);
                    return 5;
                }
            }
        }
        int childExitCode = child.ExitCode;
        await output.WriteLineAsync(JsonSerializer.Serialize(new { launchId, processId, state = "exited", childExitCode, cancelled, forcedTerminationRequested = forced })).ConfigureAwait(false);
        return cancelled ? 130
                         : childExitCode;
    }

    /// <summary>shell展開をせず二つの明示パスだけを子の引数へ渡します。</summary>
    /// <returns>所有consoleを引き継ぐ実起動設定です。</returns>
    private static ProcessStartInfo Parse(string[] args)
    {
        if (args.Length != 6)
        {
            throw new ArgumentException("two options required", nameof(args));
        }
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        options.Add(args[2], args[3]);
        options.Add(args[4], args[5]);
        string executableOption = "--executable";
        string configOption = "--config";
        if (!options.TryGetValue(executableOption, out string? executable) || !options.TryGetValue(configOption, out string? config))
        {
            throw new ArgumentException("explicit paths required", nameof(args));
        }
        executable = Path.GetFullPath(executable);
        config = Path.GetFullPath(config);
        if (!File.Exists(executable) || !File.Exists(config))
        {
            throw new ArgumentException("file missing", nameof(args));
        }
        ProcessStartInfo start = new(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        return start;
    }

    /// <summary>取消と独立した有限時間で実終了を確認します。</summary>
    /// <returns>終了確認時だけtrueです。</returns>
    private static async Task<bool> WaitAsync(Process process, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }
}
