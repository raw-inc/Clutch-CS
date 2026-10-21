using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Persistence;

/// <summary>内部ノード状態だけを明示読取り・一回の比較保存へ接続します。</summary>
internal static class InternalDataManagement
{
    /// <summary>外部操作や任意リポジトリ領域を公開しない操作を登録します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime runtime)
    {
        registry.Read("internal-data.read", async (value, cancellation) => await ReadAsync(runtime, ManagementRegistry.Body<InternalDataTarget>(value), cancellation).ConfigureAwait(false));
        registry.Write("internal-data.write", async (value, cancellation) => await WriteAsync(runtime, ManagementRegistry.Body<InternalDataWriteRequest>(value), false, cancellation).ConfigureAwait(false));
        registry.Write("internal-data.compare-exchange", async (value, cancellation) => await WriteAsync(runtime, ManagementRegistry.Body<InternalDataWriteRequest>(value), true, cancellation).ConfigureAwait(false));
    }

    /// <summary>保存確認値を未保存の現在値で置き換えません。</summary>
    /// <returns>現在値と、強い保存から読み取れた基点です。</returns>
    private static async Task<InternalDataReadResult> ReadAsync(ServiceRuntime runtime, InternalDataTarget target, CancellationToken cancellationToken = default)
    {
        string scope = Scope(runtime, target);
        AvailabilityValue current = await runtime.Persistence.AvailableState.ReadAvailableAsync(scope, target.Key, cancellationToken).ConfigureAwait(false);
        AvailabilityPolicy policy = runtime.Persistence.Availability.Policy(Contracts.Persistence.EStoragePurpose.Runtime);
        try
        {
            VersionedState? confirmed = await runtime.State.ReadAsync(scope, target.Key, cancellationToken).ConfigureAwait(false);
            return new(current, confirmed?.Value, confirmed?.Version, confirmed?.LocalOnly != true, policy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return new(current, null, null, false, policy);
        }
    }

    /// <summary>新規の明示setと再接続の強い比較保存を区別します。</summary>
    /// <returns>元操作の保存・採用の実行結果です。</returns>
    private static async Task<InternalDataWriteResult> WriteAsync(ServiceRuntime runtime, InternalDataWriteRequest request, bool compareOnly, CancellationToken cancellationToken = default)
    {
        string scope = Scope(runtime, request.Target);
        if (request.OperationId == Guid.Empty || request.Value is null || 512 < request.ExpectedRevision?.Length)
        {
            throw new ManagementInputException();
        }
        if (!compareOnly)
        {
            AvailabilityWriteResult result = await runtime.Persistence.AvailableState.SetAvailableAsync(scope, request.Target.Key, request.Value, request.ExpectedRevision, request.OperationId, cancellationToken).ConfigureAwait(false);
            return new(request.OperationId, result);
        }
        string datasetId = Infrastructure.Persistence.Availability.AvailableNodeStateStore.DatasetId(scope, request.Target.Key);
        if (runtime.Persistence.Availability.TryGetCandidate(EStoragePurpose.Runtime, datasetId) is not null)
        {
            return new(request.OperationId, new(EOutcome.NotExecuted, false, runtime.Persistence.Availability.TryGetCurrent(EStoragePurpose.Runtime, datasetId), "Availability.CandidatePending"));
        }
        StateWriteResult written = await runtime.State.CompareExchangeAsync(scope, request.Target.Key, request.ExpectedRevision, request.Value, cancellationToken).ConfigureAwait(false);
        if (written.Outcome != EOutcome.Succeeded || !written.Applied)
        {
            AvailabilityValue observed = await runtime.Persistence.AvailableState.ReadAvailableAsync(scope, request.Target.Key, cancellationToken).ConfigureAwait(false);
            return new(request.OperationId, new(written.Outcome, written.Applied, observed, written.Reason));
        }
        AvailabilityState state = new(EStoragePurpose.Runtime, Infrastructure.Persistence.Availability.AvailableNodeStateStore.DatasetId(scope, request.Target.Key), runtime.Cluster.LocalPeerId,
            written.Current?.Version, written.Current?.Version, written.Current?.Version, IsLocalUnconfirmed: written.LocalOnly);
        AvailabilityValue current = new(written.Current?.Value, written.Current?.Version, state);
        if (written.Outcome == EOutcome.Succeeded && written.Applied)
        {
            if (written.LocalOnly)
            {
                current = await runtime.Persistence.Availability.RememberLocalBranchAsync(EStoragePurpose.Runtime, state.DatasetId, written.Current?.Value, written.Current?.Version, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                current = await runtime.Persistence.Availability.RememberConfirmedAsync(EStoragePurpose.Runtime, state.DatasetId, written.Current?.Value, written.Current?.Version, true, cancellationToken).ConfigureAwait(false);
            }
        }
        return new(request.OperationId, new(written.Outcome, written.Applied, current, written.Reason));
    }

    /// <summary>予約領域や別Peerへの任意パスを入力から作れないようにします。</summary>
    /// <returns>このPeerかワークフロー共有のノード専用範囲です。</returns>
    private static string Scope(ServiceRuntime runtime, InternalDataTarget target)
    {
        if (target?.IsValid() != true)
        {
            throw new ManagementInputException();
        }
        if (target.Scope == EInternalDataScope.Cluster)
        {
            return NodeStateScope.Cluster;
        }
        if (target.Scope == EInternalDataScope.Shared)
        {
            return NodeStateScope.Shared(target.WorkflowId);
        }
        return NodeStateScope.Peer(runtime.Cluster.LocalPeerId, target.WorkflowId);
    }
}
