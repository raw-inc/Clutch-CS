using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>共有保持方針をローカル保存先へ反映し、再起動時の旧方針による削除を防ぎます。</summary>
public sealed class LogRetentionCoordinator(IDurableRepository repository, WorkflowLogService log) : IDisposable
{
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool initialized;
    private LogRetentionStatus status = new(null, null, "LogRetention.Initializing", true);
    private string Scope => "peer:" + log.Options.PeerId + ":log-retention";

    /// <summary>このPeerで実際に確認できた反映状態です。</summary>
    public LogRetentionStatus Status => Volatile.Read(ref status);

    /// <summary>既存の検証と既定値を使ってPeer非依存の保持方針を作成します。</summary>
    /// <returns>全レベルが明示された共有候補です。</returns>
    public static LogRetentionSettings FromOptions(LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LogOptions normalized = new LogPolicy(options).Options;
        return new(normalized.Retention!, normalized.RollingTime, normalized.TimeZoneId);
    }

    /// <summary>確認済みローカル方針を先に復元し、共有方針の適用と記録後に保守を再開します。</summary>
    /// <returns>適用できない場合も本来のワークフローを止めず状態へ記録します。</returns>
    public async Task ReconcileAsync(ClusterOperationalSnapshot? shared, CancellationToken cancellationToken = default)
    {
        if (shared is null)
        {
            return;
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!initialized)
            {
                log.RetentionHeld = true;
                DurableEntry? retained = await repository.ReadAsync(Scope, "current", cancellationToken).ConfigureAwait(false);
                LogRetentionSettings? previous = retained?.Value?.Json.Deserialize<LogRetentionSettings>();
                if (previous is not null)
                {
                    await ApplyAsync(previous, cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref status, new(ContractJson.Hash(previous), previous, null, true));
                }
                initialized = true;
            }
            if (shared.UnresolvedFields?.Contains(nameof(ClusterOperationalSettings.LogRetention), StringComparer.Ordinal) == true
                || shared.UnresolvedFields?.Contains(nameof(ClusterOperationalSettings.DataRetention), StringComparer.Ordinal) == true)
            {
                log.RetentionHeld = true;
                Volatile.Write(ref status, Status with { Problem = "LogRetention.Conflict", MaintenanceHeld = true });
                return;
            }
            LogRetentionSettings? desired = shared.Settings.LogRetention;
            if (shared.Settings.DataRetention is not null)
            {
                desired = HistoryRetentionPolicy.Logs(HistoryRetentionPolicy.Resolve(shared.Settings, log.Options))
                    with { ExpandCompressed = shared.Settings.LogRetention?.ExpandCompressed ?? false };
            }
            if (desired is null)
            {
                log.RetentionHeld = false;
                Volatile.Write(ref status, Status with { Problem = "LogRetention.LegacyLocalPolicy", MaintenanceHeld = false });
                return;
            }
            string hash = ContractJson.Hash(desired);
            if (!string.Equals(Status.AppliedHash, hash, StringComparison.Ordinal))
            {
                log.RetentionHeld = true;
                await ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
                EOutcome saved = await repository.WriteLocalSnapshotAsync(Scope, "current", NodeValue.FromObject(desired), cancellationToken).ConfigureAwait(false);
                if (saved != EOutcome.Succeeded)
                {
                    Volatile.Write(ref status, Status with { Problem = "LogRetention.AppliedRecordUnconfirmed", MaintenanceHeld = true });
                    return;
                }
            }
            log.RetentionHeld = false;
            Volatile.Write(ref status, new(hash, desired, null, false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or JsonException or ArgumentException or InvalidOperationException)
        {
            log.RetentionHeld = true;
            Volatile.Write(ref status, Status with { Problem = "LogRetention.ApplyUnconfirmed", MaintenanceHeld = true });
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>保存先・接続設定を維持し、保持値だけを既存の実処理へ渡します。</summary>
    /// <returns>ローカルの保持設定変更です。</returns>
    private Task ApplyAsync(LogRetentionSettings desired, CancellationToken cancellationToken = default)
        => log.UpdateOptionsAsync(log.Options with { Retention = desired.Rules, RollingTime = desired.RollingTime, TimeZoneId = desired.TimeZoneId },
            new(desired.ApplyToExisting, desired.ExpandCompressed), cancellationToken);

    /// <summary>Hostの終了後にローカル適用の排他を破棄します。</summary>
    public void Dispose() => changes.Dispose();
}
