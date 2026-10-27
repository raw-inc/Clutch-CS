using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>現在の起動回のOSハンドルを保持し、終了後も終了コードを取得します。</summary>
internal sealed class ProcessHandleMonitor(IProcessPlatform platform) : IDisposable
{
    private readonly ConcurrentDictionary<string, TrackedProcess> handles = new(StringComparer.Ordinal);

    /// <summary>起動と通常終了要求に同じOS境界を使用します。</summary>
    internal IProcessPlatform Platform => platform;

    /// <summary>起動済みハンドルの所有権を引き取り、次回の起動まで保持します。</summary>
    /// <returns>PIDの再利用を区別できる起動実体です。</returns>
    // 抑制理由: 終了コード取得のためハンドルを監視所有者へ移し、移譲失敗時だけこのメソッドで解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "ハンドルは登録ごとに保持し、置換または監視所有者のDisposeで回収します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "所有権移譲に失敗したハンドルだけをfinallyで回収します。")]
    internal ProcessIdentity Start(ProcessDefinition definition)
    {
        Process? process = null;
        try
        {
            process = platform.Start(definition);
            ProcessIdentity identity = ProcessOs.Identity(process);
            Replace(definition.Id, new(identity, process));
            process = null;
            return identity;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>既存の登録実体にもハンドルを確保し、観測不能を終了コード0にしません。</summary>
    /// <returns>確認できた生存状態と終了コードです。</returns>
    // 抑制理由: 登録の現起動回だけを保持し、移譲できなかったハンドルはfinallyで必ず回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "サービス再起動後に開いたハンドルも監視所有者へ移譲します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "未移譲ハンドルを終了経路によらずfinallyで回収します。")]
    internal (bool? Running, int? ExitCode, bool ExitObserved) Observe(string id, ProcessIdentity? identity)
    {
        if (identity is null)
        {
            return (false, null, false);
        }
        Process? opened = null;
        bool exited = false;
        try
        {
            if (!handles.TryGetValue(id, out TrackedProcess? tracked) || tracked.Identity != identity)
            {
                opened = Process.GetProcessById(identity.ProcessId);
                if (ProcessOs.Identity(opened) != identity)
                {
                    return (false, null, false);
                }
                if (opened.HasExited)
                {
                    return (false, null, false);
                }
                _ = opened.SafeHandle;
                tracked = new(identity, opened);
                Replace(id, tracked);
                opened = null;
            }
            if (tracked.Process.HasExited)
            {
                exited = true;
                return (false, tracked.Process.ExitCode, true);
            }
            tracked.LastAlive = DateTimeOffset.UtcNow;
            return (true, null, false);
        }
        catch (ArgumentException)
        {
            return (false, null, false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return (exited ? false
                           : ProcessOs.IsRunning(identity), null, exited);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    /// <summary>既に併存していた別プロセスを後継と誤認しないための実OS観測時刻です。</summary>
    /// <returns>この所有者が最後に生存を確認した時刻です。</returns>
    internal DateTimeOffset? LastObservedAlive(string id) => handles.GetValueOrDefault(id)?.LastAlive;

    /// <summary>起動時または監視再開時のセッションを、自律再起動の照合区間へ引き継ぎます。</summary>
    /// <returns>Windowsのセッション、または取得不能です。</returns>
    internal int? Session(string id) => handles.GetValueOrDefault(id)?.SessionId;

    /// <summary>生存と開始時刻を再確認できた後継だけへハンドルを切り替えます。</summary>
    /// <returns>一意に照合した実体を監視へ取り込めたかです。</returns>
    // 抑制理由: 生存を確認したハンドルだけを所有辞書へ移し、失敗経路ではfinallyで必ず回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "生存確認に成功した後継のハンドルを監視所有者へ移譲します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "移譲しなかったハンドルをfinallyで回収します。")]
    internal bool TryAdopt(string id, ProcessIdentity identity)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            if (ProcessOs.Identity(process) != identity || process.HasExited)
            {
                return false;
            }
            _ = process.SafeHandle;
            Replace(id, new(identity, process) { LastAlive = DateTimeOffset.UtcNow });
            process = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>同じ登録の古いハンドルだけを解放します。プロセス自体は終了させません。</summary>
    private void Replace(string id, TrackedProcess next)
    {
        using Process? previous = handles.GetValueOrDefault(id)?.Process;
        handles[id] = next;
    }

    /// <summary>削除した登録のハンドルだけを解放し、プロセスは停止しません。</summary>
    internal void Forget(string id)
    {
        if (handles.TryRemove(id, out TrackedProcess? removed))
        {
            using Process process = removed.Process;
        }
    }

    /// <summary>所有したハンドルを解放し、起動済みアプリケーションは維持します。</summary>
    public void Dispose()
    {
        foreach (TrackedProcess value in handles.Values)
        {
            value.Process.Dispose();
        }
        handles.Clear();
    }

    /// <summary>一登録につき一つだけ保持する実体とハンドルです。</summary>
    private sealed record TrackedProcess(ProcessIdentity Identity, Process Process)
    {
        internal int? SessionId
        {
            get;
        } = ReadSession(Process);

        internal DateTimeOffset LastAlive
        {
            get;
            set;
        } = Identity.StartedAt;

        /// <summary>短命な実体で追加情報を取得できなくても、確保した終了コードの監視を維持します。</summary>
        /// <returns>観測できたWindowsのセッションです。</returns>
        private static int? ReadSession(Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            try
            {
                return process.SessionId;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
