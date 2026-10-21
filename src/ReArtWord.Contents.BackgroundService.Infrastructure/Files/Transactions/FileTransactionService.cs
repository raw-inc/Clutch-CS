using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>ファイル全対象の退避・元実行・補償・解放を調整します。</summary>
public sealed class FileTransactionService : IFileTransactionDispatcher, IDisposable
{
    private readonly PeerCluster cluster;
    private readonly FileTransactionJournal journal;
    private readonly IDurableRepository repository;
    private readonly ClusterFileGate gate;
    private readonly FileStageClient client;
    private readonly FileExecutionBoundary execution;
    private readonly FileNodeEndpoint endpoint;
    private readonly EFileConflictPolicy policy;
    private readonly ConcurrentDictionary<Guid, byte> operations = [];

    /// <summary>各実行地点の能力と共通の予約・保存を接続します。</summary>
    public FileTransactionService(NodeRegistry registry, PeerCluster cluster, IDurableRepository repository, FileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        this.cluster = cluster;
        this.repository = repository;
        journal = new FileTransactionJournal(repository, cluster);
        policy = options.ConflictPolicy;
        gate = new ClusterFileGate(cluster, repository);
        client = new FileStageClient(cluster, repository);
        endpoint = new(registry, cluster, repository, new FileAccessPolicy(options));
        execution = new FileExecutionBoundary(cluster, repository, gate, endpoint, client, policy);
    }

    /// <summary>全対象transactionの準備・実変更にも通常ノードと同じ依存検査を適用します。</summary>
    public Func<string, NodeDefinition, string?>? DependencyCheck
    {
        get => endpoint.DependencyCheck;
        set => endpoint.DependencyCheck = value;
    }

    /// <summary>離脱側の新規ファイル操作へ適用する種類別許可です。</summary>
    public DetachedOperationOptions DetachedOptions
    {
        get => endpoint.DetachedOptions;
        set => endpoint.DetachedOptions = value;
    }

    /// <summary>Keep・AI・mediaも共通ファイル予約で包みます。</summary>
    /// <returns>元のノード実処理結果。</returns>
    public ValueTask<NodeResult> ExecuteFileAsync(IActionNode node, NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        return execution.ExecuteAsync(node, context, cancellationToken);
    }

    /// <summary>元実体の停止とアクセス終了の明示確認を満たす予約だけを解放します。元要求は再実行しません。</summary>
    /// <returns>元実行結果を維持した予約解放の確認です。</returns>
    public Task<FilePhaseResult> ReleaseReservationAsync(FileReservationReleaseRequest request, CancellationToken cancellationToken = default)
        => FileReservationRecovery.ReleaseConfirmedAsync(cluster, repository, gate, request, cancellationToken);

    /// <summary>現役予約に対応する終了記録だけを確認し、別パスへの次回実行を待たず回収します。</summary>
    /// <returns>実アクセスの終了を確認できた予約の回収です。</returns>
    public Task<bool> RecoverReservationsAsync(bool localOnly, CancellationToken cancellationToken = default)
        => gate.RecoverEndedAsync(localOnly, cancellationToken);

    /// <summary>終了時も未確認の予約を消さず、旧版が読める台帳へ戻します。</summary>
    /// <returns>新規予約受付の終了と互換保存です。</returns>
    public Task PrepareStopAsync(CancellationToken cancellationToken = default) => gate.PrepareStopAsync(cancellationToken);

    /// <summary>一度だけ固定要求を保存して全対象処理を開始します。</summary>
    /// <returns>実行と補償を分けた結果。</returns>
    public async Task<FileTransactionResult> ExecuteAsync(FileTransactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        if (request.Targets.Select(target => target.Operational?.Revision).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            return new FileTransactionResult(request.OperationId, EOutcome.NotExecuted, [], "File.OperationalVersionMismatch");
        }
        if (!operations.TryAdd(request.OperationId, 0))
        {
            return new FileTransactionResult(request.OperationId, EOutcome.Unknown, [], "File.InProgress");
        }
        try
        {
            FileTransactionOperation? existing = await journal.ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    return existing.Result with { Outcome = EOutcome.Failed, Reason = "File.TransactionMismatch" };
                }
                return existing.Result;
            }
            PeerInfo[] members = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray();
            PeerInfo local = cluster.GetPeers().Single(peer => peer.InstanceId == cluster.LocalInstanceId);
            if (local.Status == EPeerStatus.Detached && request.Targets.All(target => target.TargetInstanceId == local.InstanceId))
            {
                members = [local];
            }
            FileTransactionOperation operation = new(ContractJson.Hash(request), request, members, cluster.LocalPeerId, cluster.LocalInstanceId, null, new FileTransactionResult(request.OperationId, EOutcome.Unknown, request.Targets.Select(Empty).ToArray(), "File.InProgress"));
            await journal.BeginAsync(operation, cancellationToken).ConfigureAwait(false);
            return await RunAsync(operation, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new FileTransactionResult(request.OperationId, EOutcome.Unknown, request.Targets.Select(Empty).ToArray(), "File.AdmissionUnconfirmed");
        }
        finally
        {
            operations.TryRemove(request.OperationId, out _);
        }
    }

    /// <summary>元の要求を再実行せず、保存した操作記録から明示回復します。</summary>
    /// <returns>確認できた元操作の結果。</returns>
    public async Task<FileTransactionResult?> RecoverAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!operations.TryAdd(operationId, 0))
        {
            return new FileTransactionResult(operationId, EOutcome.Unknown, [], "File.InProgress");
        }
        try
        {
            FileTransactionOperation? operation = await journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation is null || operation.Result.Outcome != EOutcome.Unknown)
            {
                return operation?.Result;
            }
            return await RunAsync(operation, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>副作用を開始しない操作照会です。</summary>
    /// <returns>保存された全段階。</returns>
    public async Task<FileTransactionResult?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) => (await journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false))?.Result;

    /// <summary>準備完了までは全対象を変更せず、Unknownは補償と競合させません。</summary>
    /// <returns>確認済みの段階結果。</returns>
    private async Task<FileTransactionResult> RunAsync(FileTransactionOperation operation, bool recover, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!MembersRemain(operation.Members) || operation.Request.Targets.Count == 0 || operation.Request.Targets.Select(target => target.Id).Distinct().Count() != operation.Request.Targets.Count)
            {
                return await FinishAsync(operation, EOutcome.Unknown, "File.FixedTargetsUnavailable", cancellationToken).ConfigureAwait(false);
            }
            if (operation.Lease is null)
            {
                if (recover)
                {
                    return operation.Result with { Reason = "File.ReservationUnconfirmed" };
                }
                List<FileFootprint> footprints = [];
                foreach (RemoteExecutionRequest request in operation.Request.Targets)
                {
                    PeerInfo target = Target(operation, request);
                    ClusterResponse response = await client.InspectAsync("file.transaction.describe", target, NodeValue.FromObject(request), cancellationToken).ConfigureAwait(false);
                    FilePathAccess[]? paths = response.Value?.Json.Deserialize<FilePathAccess[]>();
                    if (response.Outcome != EOutcome.Succeeded || paths is null)
                    {
                        return await FinishAsync(operation, EOutcome.NotExecuted, "File.PreflightUnavailable", cancellationToken).ConfigureAwait(false);
                    }
                    IReadOnlyList<FileFootprint> current = await execution.ResolveAsync(paths, cancellationToken).ConfigureAwait(false);
                    if (footprints.Any(first => current.Any(second => FileOverlap.Conflicts(first, second))))
                    {
                        return await FinishAsync(operation, EOutcome.NotExecuted, "File.TargetsOverlap", cancellationToken).ConfigureAwait(false);
                    }
                    footprints.AddRange(current);
                }
                EFileConflictPolicy fixedPolicy = policy;
                if (operation.Request.Targets.Count != 0)
                {
                    fixedPolicy = operation.Request.Targets[0].Operational?.Settings.FileConflictPolicy ?? policy;
                }
                FileLeaseResult acquired = await gate.AcquireFixedAsync(operation.Request.OperationId, footprints, fixedPolicy, operation.Members, cancellationToken).ConfigureAwait(false);
                operation = operation with { Lease = acquired.Lease, ReservationConfirmed = acquired.Outcome == EOutcome.Succeeded, Result = operation.Result with { Reservation = acquired.Lease } };
                await journal.SaveAsync(operation, cancellationToken).ConfigureAwait(false);
                if (acquired.Outcome != EOutcome.Succeeded)
                {
                    return await FinishAsync(operation, acquired.Outcome, acquired.Reason, cancellationToken).ConfigureAwait(false);
                }
            }
            if (!operation.ReservationConfirmed)
            {
                FileLeaseResult confirmed = await gate.ConfirmAsync(operation.Lease!, cancellationToken).ConfigureAwait(false);
                if (confirmed.Outcome != EOutcome.Succeeded)
                {
                    return await FinishAsync(operation, EOutcome.Unknown, "File.ReservationUnconfirmed", cancellationToken).ConfigureAwait(false);
                }
                operation = operation with { ReservationConfirmed = true };
                await journal.SaveAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            FileTargetTransactionResult[] results = operation.Result.Targets.ToArray();
            for (int index = 0; index < results.Length; index++)
            {
                RemoteExecutionRequest request = operation.Request.Targets[index];
                if (results[index].Preparation.Outcome != EOutcome.Succeeded)
                {
                    ClusterResponse captured = await client.InvokeAsync(FileStageClient.Identity(request.Id, "capture"), "file.transaction.capture", Target(operation, request), NodeValue.FromObject(new FileCaptureCommand(request, operation.Lease!)), operation.Members, cancellationToken).ConfigureAwait(false);
                    results[index] = results[index] with { Preparation = new FilePhaseResult(captured.Outcome, captured.Reason), Backup = captured.Value?.Json.Deserialize<FileCompensationToken>() };
                    operation = operation with { Result = operation.Result with { Targets = results.ToArray() } };
                    await journal.SaveAsync(operation, cancellationToken).ConfigureAwait(false);
                    if (captured.Outcome != EOutcome.Succeeded)
                    {
                        if (captured.Outcome != EOutcome.Unknown)
                        {
                            operation = await ApplyTokensAsync(operation, "release", cancellationToken).ConfigureAwait(false);
                            if (operation.Result.Targets.All(target => target.Backup is null || target.Release.Outcome == EOutcome.Succeeded) && (await gate.ReleaseAsync(operation.Lease!, cancellationToken).ConfigureAwait(false)).Outcome == EOutcome.Succeeded)
                            {
                                return await FinishAsync(operation, EOutcome.NotExecuted, "File.PreparationFailed", cancellationToken).ConfigureAwait(false);
                            }
                        }
                        return await FinishAsync(operation, EOutcome.Unknown, "File.PreparationUnconfirmed", cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            NodeResult[] executions = await Task.WhenAll(operation.Request.Targets.Select(request => ExecuteTargetAsync(request, recover, cancellationToken))).ConfigureAwait(false);
            for (int index = 0; index < results.Length; index++)
            {
                results[index] = results[index] with { Execution = executions[index] };
            }
            operation = operation with { Result = operation.Result with { Targets = results } };
            await journal.SaveAsync(operation, cancellationToken).ConfigureAwait(false);
            if (!MembersRemain(operation.Members) || (results.Any(target => target.Execution.Outcome == EOutcome.Unknown) && !await AllFinishedAsync(operation, cancellationToken).ConfigureAwait(false)))
            {
                return await FinishAsync(operation, EOutcome.Unknown, "File.ExecutionUnconfirmed", cancellationToken).ConfigureAwait(false);
            }
            bool failed = results.Any(target => target.Execution.Outcome != EOutcome.Succeeded);
            if (failed)
            {
                operation = await ApplyTokensAsync(operation, "restore", cancellationToken).ConfigureAwait(false);
                if (operation.Result.Targets.Any(target => target.Compensation.Outcome != EOutcome.Succeeded))
                {
                    return await FinishAsync(operation, EOutcome.Unknown, "File.CompensationUnconfirmed", cancellationToken).ConfigureAwait(false);
                }
            }
            operation = await ApplyTokensAsync(operation, "release", cancellationToken).ConfigureAwait(false);
            if (operation.Result.Targets.Any(target => target.Release.Outcome != EOutcome.Succeeded))
            {
                return await FinishAsync(operation, EOutcome.Unknown, "File.ReleaseUnconfirmed", cancellationToken).ConfigureAwait(false);
            }
            FilePhaseResult released = await gate.ReleaseAsync(operation.Lease!, cancellationToken).ConfigureAwait(false);
            operation = operation with { Result = operation.Result with { ReservationRelease = released } };
            EOutcome outcome = EOutcome.Succeeded;
            if (failed)
            {
                outcome = EOutcome.Failed;
            }
            if (released.Outcome != EOutcome.Succeeded)
            {
                outcome = EOutcome.Unknown;
            }
            return await FinishAsync(operation, outcome, released.Reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return operation.Result with { Outcome = EOutcome.Unknown, Reason = "File.TransactionInterrupted" };
        }
    }

    /// <summary>補償と解放は元実行と異なる固定段階IDで実施します。</summary>
    /// <returns>各対象の実段階結果。</returns>
    private async Task<FileTransactionOperation> ApplyTokensAsync(FileTransactionOperation operation, string phase, CancellationToken cancellationToken = default)
    {
        FileTargetTransactionResult[] results = operation.Result.Targets.ToArray();
        string restore = "restore";
        for (int index = 0; index < results.Length; index++)
        {
            if (results[index].Backup is null)
            {
                continue;
            }
            RemoteExecutionRequest request = operation.Request.Targets[index];
            ClusterResponse response = await client.InvokeAsync(FileStageClient.Identity(request.Id, phase), "file.transaction." + phase, Target(operation, request), NodeValue.FromObject(new FileTokenCommand(request, results[index].Backup!)), operation.Members, cancellationToken).ConfigureAwait(false);
            FilePhaseResult result = new(response.Outcome, response.Reason, FileStageClient.Identity(request.Id, phase));
            if (string.Equals(phase, restore, StringComparison.Ordinal))
            {
                results[index] = results[index] with { Compensation = result };
            }
            else
            {
                results[index] = results[index] with { Release = result };
            }
        }
        FileTransactionOperation updated = operation with { Result = operation.Result with { Targets = results } };
        await journal.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>元操作の結果を保存して返します。</summary>
    /// <returns>最終または確認不能の結果。</returns>
    private async Task<FileTransactionResult> FinishAsync(FileTransactionOperation operation, EOutcome outcome, string? reason, CancellationToken cancellationToken = default)
    {
        FileTransactionOperation updated = operation with { Result = operation.Result with { Outcome = outcome, Reason = reason } };
        await journal.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated.Result;
    }

    /// <summary>新規実行と読取り専用の明示回復を区別します。</summary>
    /// <returns>元要求の実行結果。</returns>
    private async Task<NodeResult> ExecuteTargetAsync(RemoteExecutionRequest request, bool recover, CancellationToken cancellationToken = default)
    {
        if (!recover)
        {
            return await cluster.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        ClusterRequest command = new(request.Id, "node.execute", request.TargetPeerId, request.TargetInstanceId, request.ExpiresAt, NodeValue.FromObject(request));
        ClusterRequestStatus status = await cluster.QueryRequestAsync(command, cancellationToken).ConfigureAwait(false);
        return status.Response?.Value?.Json.Deserialize<NodeResult>() ?? NodeResult.Failure(status.Reason ?? "File.ExecutionUnconfirmed", "元の実行結果を確認できません。", EOutcome.Unknown);
    }

    /// <summary>補償が実行中の書込みと競合しないことを元操作記録で確認します。</summary>
    /// <returns>全ての元実行タスクがノード結果を保存して終了したか。</returns>
    private async Task<bool> AllFinishedAsync(FileTransactionOperation operation, CancellationToken cancellationToken = default)
    {
        foreach (RemoteExecutionRequest request in operation.Request.Targets)
        {
            ClusterRequestStatus status = await cluster.QueryRequestAsync(new ClusterRequest(request.Id, "node.execute", request.TargetPeerId, request.TargetInstanceId, request.ExpiresAt, NodeValue.FromObject(request)), cancellationToken).ConfigureAwait(false);
            if (status.Running || status.Response?.Value is null)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>元の実体が引き続き通常参加しているか確認します。</summary>
    /// <returns>確認対象を縮小せず進められるか。</returns>
    private bool MembersRemain(IReadOnlyList<PeerInfo> members) => members.All(member => cluster.GetPeers().Any(peer => peer.InstanceId == member.InstanceId && (PeerStatusRules.CanParticipate(peer.Status) || (member.Status == EPeerStatus.Detached && peer.Status == EPeerStatus.Detached))));

    /// <summary>実行先を元の実体へ固定します。</summary>
    /// <returns>元の対象実体。</returns>
    private static PeerInfo Target(FileTransactionOperation operation, RemoteExecutionRequest request) => operation.Members.Single(peer => peer.InstanceId == request.TargetInstanceId && string.Equals(peer.PeerId, request.TargetPeerId, StringComparison.Ordinal));

    /// <summary>未実行の段階を明示して開始します。</summary>
    /// <returns>対象の初期結果。</returns>
    private static FileTargetTransactionResult Empty(RemoteExecutionRequest request) => new(request.TargetPeerId, request.TargetInstanceId, request.Id, NodeResult.Failure("File.NotStarted", "全対象の退避確認前です。", EOutcome.NotExecuted), new(EOutcome.NotExecuted), new(EOutcome.NotExecuted), new(EOutcome.NotExecuted));

    /// <summary>調停の所有資源を解放します。</summary>
    public void Dispose()
    {
        gate.Dispose();
    }
}
