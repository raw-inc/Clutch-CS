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
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>各Peerの実ファイル許可とローカル補償退避を接続します。</summary>
internal sealed class FileNodeEndpoint
{
    private readonly NodeRegistry registry;
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly FileAccessPolicy policy;
    private string PreparedScope => "peer:" + cluster.LocalPeerId + ":file-prepared";

    /// <summary>準備と実変更の開始時だけ、内容競合の依存範囲を確認します。</summary>
    internal Func<string, NodeDefinition, string?>? DependencyCheck
    {
        get; set;
    }

    /// <summary>明示離脱中の新規操作だけへ適用する種類別許可です。</summary>
    internal DetachedOperationOptions DetachedOptions
    {
        get; set;
    } = new();

    /// <summary>直接の準備RPCでも通常ノードと同じ離脱用途制限を確認します。</summary>
    /// <returns>開始不可理由です。</returns>
    private async ValueTask<string?> StartProblemAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        string? problem = ExecutionProblem(request);
        INode? node = Find(request);
        if (problem is null && cluster.IsDetached && node is not null)
        {
            problem = await DetachedOperationPolicy.GetProblemAsync(node, Context(request), DetachedOptions, cancellationToken).ConfigureAwait(false);
        }
        return problem;
    }

    /// <summary>Keep/AI/mediaの直接呼出しも実footprintに基づき新規変更を検査します。</summary>
    /// <returns>開始不可理由です。</returns>
    internal async ValueTask<string?> StartProblemAsync(IActionNode node, NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        string? problem = ExecutionProblem(node, context);
        if (problem is null && cluster.IsDetached)
        {
            problem = await DetachedOperationPolicy.GetProblemAsync(node, context, DetachedOptions, cancellationToken).ConfigureAwait(false);
        }
        return problem;
    }

    /// <summary>補償を止めず、新たな副作用の依存条件だけを確認します。</summary>
    /// <returns>未実行理由、利用可能ならnullです。</returns>
    internal string? ExecutionProblem(RemoteExecutionRequest request)
    {
        INode? node = Find(request);
        string? problem = DependencyCheck?.Invoke(request.WorkflowId, request.Node);
        if (problem is null && node is not null)
        {
            problem = OperationalDependencyPolicy.GetProblem(request.Operational, request.Node, node.Descriptor, node is IFileFootprintNode);
        }
        return problem;
    }

    /// <summary>通常実行のファイル用途も同じ依存境界へ渡します。</summary>
    /// <returns>未実行理由、利用可能ならnullです。</returns>
    internal string? ExecutionProblem(IActionNode node, NodeExecutionContext context)
    {
        NodeDefinition definition = new(context.NodeId, node.Descriptor.Type, context.Settings);
        return DependencyCheck?.Invoke(context.WorkflowId, definition) ?? OperationalDependencyPolicy.GetProblem(context.Operational, definition, node.Descriptor, node is IFileFootprintNode);
    }

    /// <summary>対象Peerでだけ動作するファイル段階を登録します。</summary>
    public FileNodeEndpoint(NodeRegistry registry, PeerCluster cluster, IDurableRepository repository, FileAccessPolicy policy)
    {
        this.registry = registry;
        this.cluster = cluster;
        this.repository = repository;
        this.policy = policy;
        cluster.RegisterInspection("file.transaction.describe", DescribeAsync);
        cluster.RegisterInspection("file.transaction.path", ValidatePathAsync);
        cluster.RegisterDeferredHandler("file.transaction.capture", CaptureAsync);
        cluster.RegisterDeferredHandler("file.transaction.restore", RestoreAsync);
        cluster.RegisterDeferredHandler("file.transaction.release", ReleaseAsync);
    }

    /// <summary>全対象準備の予約がこの一件を所有しているか確認します。</summary>
    /// <returns>元要求に対応する保持中の退避です。</returns>
    public async Task<PreparedFileOperation?> ReadPreparedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(PreparedScope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<PreparedFileOperation>();
    }

    /// <summary>読み取り元を実Hostと正規パスへ変換します。</summary>
    /// <returns>実行地点が検証した範囲。</returns>
    public FileFootprint Resolve(FilePathAccess access)
    {
        if (!string.Equals(access.PeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("ファイル範囲の所有Peerが一致しません。");
        }
        string path = policy.ResolvePath(access.Path, !access.Write || access.DirectoryEntriesOnly);
        if (access.DirectoryEntriesOnly && !Directory.Exists(path))
        {
            throw new IOException("内部生成物の親ディレクトリを確認できません。");
        }
        if (OperatingSystem.IsWindows())
        {
            path = path.Replace('\\', '/').ToUpperInvariant();
        }
        PeerInfo peer = cluster.GetPeers().Single(item => item.InstanceId == cluster.LocalInstanceId);
        return new FileFootprint(peer.PeerId, peer.InstanceId, peer.HostId, path.TrimEnd('/'), access.Write);
    }

    /// <summary>元の実行設定で許可検証したアクセス範囲を宣言します。</summary>
    /// <returns>sourceとdestinationの一覧。</returns>
    private async Task<ClusterResponse> DescribeAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        RemoteExecutionRequest request = command.Payload.Json.Deserialize<RemoteExecutionRequest>() ?? throw new InvalidDataException("実行要求がありません。");
        string? problem = await StartProblemAsync(request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return new(EOutcome.NotExecuted, Reason: problem);
        }
        if (Find(request) is not IFileFootprintNode node)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "File.FootprintUnavailable");
        }
        IReadOnlyList<FilePathAccess> paths = await node.GetFileAccessAsync(Context(request), cancellationToken).ConfigureAwait(false);
        return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(paths));
    }

    /// <summary>別PeerからのパスをこのPeerの規則だけで検証します。</summary>
    /// <returns>正規化した読書き範囲。</returns>
    private Task<ClusterResponse> ValidatePathAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FilePathAccess access = command.Payload.Json.Deserialize<FilePathAccess>() ?? throw new InvalidDataException("ファイル範囲がありません。");
        return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(Resolve(access))));
    }

    /// <summary>準備開始を保存してからローカル退避とリースを取得します。</summary>
    /// <returns>確認した退避トークンです。</returns>
    private async Task<ClusterResponse> CaptureAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        FileCaptureCommand capture = command.Payload.Json.Deserialize<FileCaptureCommand>() ?? throw new InvalidDataException("退避要求がありません。");
        string? problem = await StartProblemAsync(capture.Request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return new(EOutcome.NotExecuted, Reason: problem);
        }
        if (Find(capture.Request) is not ICompensableFileNode node)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "File.CompensationUnavailable");
        }
        if (node is not IFileFootprintNode footprintNode || !await ReservationCoversAsync(capture, footprintNode, cancellationToken).ConfigureAwait(false))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "File.ReservationNotHeld");
        }
        PreparedFileOperation? existing = await ReadPreparedAsync(capture.Request.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ClusterResponse(EOutcome.Unknown, Reason: "File.CaptureAlreadyStarted");
        }
        PreparedFileOperation prepared = new(capture.Request, capture.Lease);
        DurableReceipt started = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(PreparedScope, capture.Request.Id.ToString("N"), null, NodeValue.FromObject(prepared))], cancellationToken).ConfigureAwait(false);
        if (started.Outcome != EOutcome.Succeeded)
        {
            return new ClusterResponse(EOutcome.Unknown, Reason: "File.CaptureStartUnconfirmed");
        }
        FileCompensationToken token;
        try
        {
            token = await node.CaptureAsync(Context(capture.Request), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "File.CaptureFailed");
        }
        if (token.ExecutionId != capture.Request.Id || token.Id == Guid.Empty)
        {
            return new ClusterResponse(EOutcome.Unknown, Reason: "File.CaptureTokenMismatch");
        }
        DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(PreparedScope, capture.Request.Id.ToString("N"), started.Entries[0].Version, NodeValue.FromObject(prepared with { Token = token }))], cancellationToken).ConfigureAwait(false);
        return new ClusterResponse(saved.Outcome, NodeValue.FromObject(token), saved.Reason);
    }

    /// <summary>元の実行前退避へ明示補償します。</summary>
    /// <returns>実補償の確認結果。</returns>
    private async Task<ClusterResponse> RestoreAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        FileTokenCommand restore = command.Payload.Json.Deserialize<FileTokenCommand>() ?? throw new InvalidDataException("補償要求がありません。");
        if (await ValidateTokenAsync(restore, cancellationToken).ConfigureAwait(false) is not ICompensableFileNode node)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "File.CompensationMismatch");
        }
        await node.RestoreAsync(restore.Token, cancellationToken).ConfigureAwait(false);
        return new ClusterResponse(EOutcome.Succeeded);
    }

    /// <summary>確認済み退避とローカルリースを解放します。</summary>
    /// <returns>実解放の確認結果。</returns>
    private async Task<ClusterResponse> ReleaseAsync(ClusterRequest command, CancellationToken cancellationToken = default)
    {
        FileTokenCommand release = command.Payload.Json.Deserialize<FileTokenCommand>() ?? throw new InvalidDataException("解放要求がありません。");
        if (await ValidateTokenAsync(release, cancellationToken).ConfigureAwait(false) is not ICompensableFileNode node)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "File.CompensationMismatch");
        }
        await node.ReleaseAsync(release.Token, cancellationToken).ConfigureAwait(false);
        DurableEntry? entry = await repository.ReadAsync(PreparedScope, release.Request.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
        PreparedFileOperation prepared = entry!.Value!.Json.Deserialize<PreparedFileOperation>()!;
        DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(PreparedScope, release.Request.Id.ToString("N"), entry.Version, NodeValue.FromObject(prepared with { Released = true }))], cancellationToken).ConfigureAwait(false);
        return new ClusterResponse(saved.Outcome, Reason: saved.Reason);
    }

    /// <summary>退避トークンと元要求が一致する場合だけ実補償へ進めます。</summary>
    /// <returns>一致した補償能力。</returns>
    private async Task<ICompensableFileNode?> ValidateTokenAsync(FileTokenCommand command, CancellationToken cancellationToken = default)
    {
        PreparedFileOperation? prepared = await ReadPreparedAsync(command.Request.Id, cancellationToken).ConfigureAwait(false);
        if (prepared?.Token != command.Token || !string.Equals(ContractJson.Hash(prepared.Request), ContractJson.Hash(command.Request), StringComparison.Ordinal))
        {
            return null;
        }
        return Find(command.Request) as ICompensableFileNode;
    }

    /// <summary>今回の実体に一致する元ノードだけを取得します。</summary>
    /// <returns>登録済みの対象能力。</returns>
    private INode? Find(RemoteExecutionRequest request)
    {
        if (!string.Equals(request.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal) || request.TargetInstanceId != cluster.LocalInstanceId)
        {
            return null;
        }
        return registry.Find(request.Node.Type);
    }

    /// <summary>このPeerにも実予約が共有済みであり、ローカル範囲が予約内にあるか検証します。</summary>
    /// <returns>実退避へ進めるか。</returns>
    private async Task<bool> ReservationCoversAsync(FileCaptureCommand capture, IFileFootprintNode node, CancellationToken cancellationToken = default)
    {
        string scope = "file:reservations";
        if (capture.Lease.LocalOnly)
        {
            if (!string.Equals(capture.Lease.OwnerPeerId, cluster.LocalPeerId, StringComparison.Ordinal))
            {
                return false;
            }
            scope = "peer:" + cluster.LocalPeerId + ":file-reservations";
        }
        Dictionary<Guid, FileLease> leases = (await FileReservationLedger.ReadAsync(repository, scope, cancellationToken).ConfigureAwait(false)).Active;
        if (!leases.TryGetValue(capture.Lease.Id, out FileLease? held) || !string.Equals(ContractJson.Hash(held), ContractJson.Hash(capture.Lease), StringComparison.Ordinal))
        {
            return false;
        }
        IReadOnlyList<FilePathAccess> accesses = await node.GetFileAccessAsync(Context(capture.Request), cancellationToken).ConfigureAwait(false);
        return accesses.Where(access => string.Equals(access.PeerId, cluster.LocalPeerId, StringComparison.Ordinal)).Select(Resolve).All(capture.Lease.Footprints.Contains);
    }

    /// <summary>退避でも実行と同じ一件識別子・設定を使います。</summary>
    /// <returns>対象実行文脈。</returns>
    private static NodeExecutionContext Context(RemoteExecutionRequest request) => new(request.Id, request.CausationId, request.WorkflowId, request.Revision, request.Node.Id, request.TargetPeerId, request.Node.Settings, request.Inputs, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById(request.Operational?.Settings.TimeZoneId ?? request.TimeZoneId ?? "UTC")) { Operational = request.Operational };
}
