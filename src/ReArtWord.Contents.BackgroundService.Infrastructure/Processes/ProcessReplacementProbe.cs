using System.Collections.Concurrent;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>終了確認と復旧の待機中に、同じ実行ファイルの一意な後継を探します。</summary>
internal sealed class ProcessReplacementProbe(Func<int, CancellationToken, Task<string[]?>>? readArguments = null)
{
    private readonly Func<int, CancellationToken, Task<string[]?>> argumentsReader = readArguments ?? ProcessArguments.ReadAsync;
    private readonly ConcurrentDictionary<string, ProbeCache> cache = new(StringComparer.Ordinal);

    /// <summary>高コストな引数照会を一秒以内に繰り返さず、同じ登録の排他内で候補を確認します。</summary>
    /// <returns>唯一の実体、候補なし、または照合不能の理由です。</returns>
    internal async ValueTask<(ProcessIdentity? Identity, string? Problem)> FindAsync(string application, ProcessReplacementWindow window, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(application, out ProbeCache? previous) && previous.Window == window && DateTimeOffset.UtcNow < previous.ExpiresAt && DateTimeOffset.UtcNow < window.Deadline)
        {
            return (previous.Identity, previous.Problem);
        }
        (ProcessIdentity? identity, string? problem) = await SearchAsync(window, cancellationToken).ConfigureAwait(false);
        cache[application] = new(window, DateTimeOffset.UtcNow.AddSeconds(1), identity, problem);
        return (identity, problem);
    }

    /// <summary>確認区間を抜けた登録の候補を即時解放します。</summary>
    internal void Clear(string application) => cache.TryRemove(application, out _);

    /// <summary>既に併存していた実体を除き、複数候補や取得不能から勝手に一つを選びません。</summary>
    /// <returns>新しい実体の照合結果です。</returns>
    private async ValueTask<(ProcessIdentity? Identity, string? Problem)> SearchAsync(ProcessReplacementWindow window, CancellationToken cancellationToken = default)
    {
        string name = Path.GetFileName(window.ExecutablePath);
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            name = Path.GetFileNameWithoutExtension(name);
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        Process[] processes = Process.GetProcessesByName(name);
        ProcessIdentity? found = null;
        string? problem = null;
        try
        {
            foreach (Process candidate in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Process process = candidate;
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }
                    ProcessIdentity identity = ProcessOs.Identity(process);
                    if (identity.StartedAt < window.NotBefore || process.HasExited)
                    {
                        continue;
                    }
                    if (window.SessionId is int session && process.SessionId != session)
                    {
                        continue;
                    }
                    string? path = process.MainModule?.FileName;
                    if (path is null)
                    {
                        if (!HasExited(process))
                        {
                            problem = "process.replacementUnconfirmed";
                        }
                        continue;
                    }
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(window.ExecutablePath), comparison))
                    {
                        continue;
                    }
                    if (window.Match == EProcessRestartMatch.ExecutableAndArguments)
                    {
                        string[]? arguments = await argumentsReader(identity.ProcessId, cancellationToken).ConfigureAwait(false);
                        if (arguments is null)
                        {
                            if (!HasExited(process))
                            {
                                problem = "process.replacementUnconfirmed";
                            }
                            continue;
                        }
                        if (!string.Equals(ContractJson.Hash(arguments), window.ArgumentsHash, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }
                    if (ProcessOs.IsRunning(identity) != true)
                    {
                        continue;
                    }
                    if (found is not null)
                    {
                        return (null, "process.replacementAmbiguous");
                    }
                    found = identity;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!HasExited(process))
                    {
                        problem = "process.replacementUnconfirmed";
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ProcessStopUnconfirmedException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    // 引数照会の異常終了・停止未確認も対象です。生存候補の照合不能を「候補なし」にしません。
                    if (!HasExited(process))
                    {
                        problem = "process.replacementUnconfirmed";
                    }
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
        if (problem is not null)
        {
            return (null, problem);
        }
        return (found, null);
    }

    /// <summary>照会中に終了した候補は除外し、生存を確認できない候補は保留に残します。</summary>
    /// <returns>OSから終了を確認できた場合だけtrueです。</returns>
    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>一登録・一確認区間だけを保持する照会抑制です。</summary>
    private sealed record ProbeCache(ProcessReplacementWindow Window, DateTimeOffset ExpiresAt, ProcessIdentity? Identity, string? Problem);
}
