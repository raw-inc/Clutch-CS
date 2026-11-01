using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>試験で起動・観測した実体のハンドルを、回収完了まで保持します。</summary>
internal sealed class TestProcessHandles : IDisposable
{
    private readonly Dictionary<ProcessIdentity, Process> handles = [];

    /// <summary>現在保持している実体です。</summary>
    internal IReadOnlyCollection<ProcessIdentity> Identities => handles.Keys;

    /// <summary>開始時刻まで照合できた実体だけを保持します。</summary>
    // 抑制理由: 照合成功時にハンドルを所有辞書へ移譲し、それ以外はfinallyで回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "確保したハンドルはStopまたはDisposeで回収します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "未移譲のハンドルをfinallyで回収します。")]
    internal void Capture(ProcessIdentity identity)
    {
        if (handles.ContainsKey(identity))
        {
            return;
        }
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            _ = process.SafeHandle;
            if (Identity(process) == identity)
            {
                handles.Add(identity, process);
                process = null;
            }
        }
        catch (ArgumentException)
        {
            // 初回観測より前に消えた実体は取得できません。既に保持している実体は再検索しません。
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>試験自身が起動したハンドルの所有権を引き取ります。</summary>
    /// <returns>確保した実体の識別子です。</returns>
    // 抑制理由: 新規ハンドルだけを辞書へ移し、重複や移譲失敗時は受け取ったハンドルを回収します。
    [SuppressMessage("SourceQuality", "RAW140", Justification = "所有権移譲に失敗したハンドルをfinallyで回収します。")]
    internal ProcessIdentity Own(Process process)
    {
        bool added = false;
        try
        {
            _ = process.SafeHandle;
            ProcessIdentity identity = Identity(process);
            added = handles.TryAdd(identity, process);
            return identity;
        }
        finally
        {
            if (!added)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>一件の回収失敗でも残りを回収し、終了確認後にだけハンドルを解放します。</summary>
    internal void Stop(int gracefulMilliseconds = 0, int forcedMilliseconds = Timeout.Infinite)
    {
        List<Exception> failures = [];
        foreach ((ProcessIdentity identity, Process process) in handles)
        {
            try
            {
                if (!process.WaitForExit(gracefulMilliseconds))
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill(true);
                        }
                        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception && process.HasExited)
                        {
                            // 自然終了と重なった場合も、同じハンドルの終了シグナルまで待ちます。
                        }
                    }
                    if (!process.WaitForExit(forcedMilliseconds))
                    {
                        throw new TimeoutException($"Fixture process exit is unconfirmed: PID={identity.ProcessId}, startedAt={identity.StartedAt:O}");
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(exception);
            }
            finally
            {
                process.Dispose();
            }
        }
        handles.Clear();
        if (failures.Count != 0)
        {
            throw new AggregateException("Some fixture processes could not be stopped; retain their files.", failures);
        }
    }

    /// <summary>停止済みの実体もOSの終了を確認してから回収します。</summary>
    public void Dispose() => Stop();

    /// <summary>PIDの再利用を区別するため、OSの開始時刻を記録します。</summary>
    /// <returns>照合する実体です。</returns>
    // 抑制理由: BCLが公開する開始時刻を取得直後にUTCへ揃えます。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "試験が所有する実体をPIDとUTC開始時刻で照合します。")]
    private static ProcessIdentity Identity(Process process) => new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
}
