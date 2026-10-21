using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Observability;

/// <summary>保持方針の変更を既存の共有保存・反映境界へ渡します。</summary>
internal static class LogRetentionManagement
{
    /// <summary>保存版を比較し、Peer固有値を変更せず保持方針だけを保存します。</summary>
    /// <returns>保存・共有・取得・反映を分けた結果です。</returns>
    internal static async Task<ClusterOperationalSaveResult> ChangeAsync(LogOptionsChangeRequest request, ServiceRuntime runtime,
        WorkflowLogService log, LogRetentionCoordinator coordinator, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("保持方針の変更には固定したoperationIdが必要です。", nameof(request));
        }
        string scope = "peer:" + runtime.Cluster.LocalPeerId + ":log-retention-requests";
        string key = request.OperationId.ToString("N");
        string fingerprint = ContractJson.Hash(request);
        DurableEntry? recorded = await runtime.Repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        RetentionRequest? known = recorded?.Value?.Json.Deserialize<RetentionRequest>(ContractJson.Options);
        if (known is not null)
        {
            if (!string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Rejected(request.OperationId, EOutcome.NotExecuted, "LogRetention.OperationMismatch");
            }
            return await runtime.Operational.GetOperationAsync(request.OperationId, cancellationToken).ConfigureAwait(false)
                ?? await runtime.Operational.SaveAsync(known.Request, cancellationToken).ConfigureAwait(false);
        }
        ClusterOperationalState state = await runtime.Operational.GetAsync(cancellationToken).ConfigureAwait(false);
        if (state.Saved is null || !string.Equals(state.Saved.Revision, request.ExpectedRevision, StringComparison.Ordinal))
        {
            return Rejected(request.OperationId, EOutcome.NotExecuted, "LogRetention.SharedRevisionChanged");
        }
        LogRetentionSettings retention = LogRetentionCoordinator.FromOptions(request.Options) with
        {
            ApplyToExisting = request.ApplyToExisting,
            ExpandCompressed = request.ExpandCompressed,
        };
        DataRetentionSettings? data = request.DataRetention ?? state.Saved.Settings.DataRetention;
        data?.Validate();
        if (request.DataRetention is not null)
        {
            retention = HistoryRetentionPolicy.Logs(request.DataRetention with { TimeZoneId = request.DataRetention.TimeZoneId ?? state.Saved.Settings.TimeZoneId })
                with { ExpandCompressed = request.ExpandCompressed };
        }
        if (request.DataRetention is null && data is not null)
        {
            Dictionary<ERetentionCategory, RetentionWindow> overrides = new(data.Overrides ?? new Dictionary<ERetentionCategory, RetentionWindow>());
            foreach (LogRetentionRule rule in retention.Rules)
            {
                ERetentionCategory category = Enum.Parse<ERetentionCategory>(rule.Level + "Log", ignoreCase: true);
                overrides[category] = new(rule.RawDays, rule.TotalDays);
            }
            data = data with { Overrides = overrides, RollingTime = retention.RollingTime, TimeZoneId = retention.TimeZoneId };
        }
        ClusterOperationalSaveRequest shared = new(request.OperationId, state.Saved.Settings with { LogRetention = retention, DataRetention = data }, request.ExpectedRevision, state.SavedActivationPolicy);
        DurableReceipt registered = await runtime.Repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, key, null, ContractJson.ToValue(new RetentionRequest(fingerprint, shared)))], cancellationToken).ConfigureAwait(false);
        if (registered.Outcome != EOutcome.Succeeded)
        {
            return Rejected(request.OperationId, EOutcome.Unknown, "LogRetention.RequestRegistrationUnconfirmed");
        }
        bool saved = false;
        try
        {
            ClusterOperationalSaveResult result = await runtime.Operational.SaveAsync(shared, cancellationToken).ConfigureAwait(false);
            saved = result.Saved.Outcome == EOutcome.Succeeded && result.Shared.Outcome == EOutcome.Succeeded;
            await coordinator.ReconcileAsync(runtime.Operational.Capture(), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            await ConfigurationAudit.WriteAsync(log, runtime.Cluster.LocalPeerId, "logs.change-options", "logs", request.Origin, saved, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>新しい保存を始めなかった場合も段階を分けた結果を返します。</summary>
    /// <returns>保持変更の保存前結果です。</returns>
    private static ClusterOperationalSaveResult Rejected(Guid operationId, EOutcome outcome, string reason)
    {
        ConfigurationPhaseResult phase = new(outcome, reason);
        return new(operationId, null, [], phase, phase, phase, phase);
    }

    /// <summary>再送時に別の共通設定版を混ぜず、元の変換要求を再利用します。</summary>
    /// <param name="Fingerprint">保持変更の元入力です。</param>
    /// <param name="Request">同じ操作IDの共有保存要求です。</param>
    private sealed record RetentionRequest(string Fingerprint, ClusterOperationalSaveRequest Request);
}
