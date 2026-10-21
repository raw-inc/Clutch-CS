using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>共通の有限Peer要求へ分割ファイル操作を接続します。</summary>
internal sealed class PeerFileTransfer : IPeerFileTransfer
{
    private readonly PeerCluster cluster;

    /// <summary>このPeerの実ファイル操作を登録します。</summary>
    public PeerFileTransfer(PeerCluster cluster, FileTransferService service)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(service);
        this.cluster = cluster;
        Register<PathRequest, FileEntryInfo>("file.stat", (request, cancellation) => service.StatAsync(request.Path, request.Operational?.Settings.FileConflictPolicy, cancellation));
        Register<PathRequest, FileListPage>("file.list", (request, cancellation) => service.ListAsync(request.Path, request.Continuation, request.Operational?.Settings.FileConflictPolicy, cancellation));
        Register<FileChunkRequest, FileChunk>("file.readChunk", service.ReadChunkAsync);
        Register<FileStageRequest, FileStageToken>("file.openStage", service.OpenStageAsync);
        RegisterVoid<FileStageDirectoryRequest>("file.stageDirectory", service.StageDirectoryAsync);
        RegisterVoid<FileChunkWriteRequest>("file.writeChunk", service.WriteChunkAsync);
        Register<FileStageToken, FileStageCommitResult>("file.commitStage", service.CommitStageAsync);
        RegisterVoid<FileStageToken>("file.discardStage", service.DiscardStageAsync);
    }

    /// <summary>ファイルの実内容版を読みます。</summary>
    /// <returns>内容の同一性です。</returns>
    public Task<FileEntryInfo> StatAsync(string peerId, string path, CancellationToken cancellationToken = default) => RequestAsync<PathRequest, FileEntryInfo>(peerId, "file.stat", new PathRequest(path), cancellationToken);

    /// <summary>元入力の共通方針で実内容版を読みます。</summary>
    /// <returns>同じ版の方針で取得した結果です。</returns>
    public Task<FileEntryInfo> StatAsync(string peerId, string path, ClusterOperationalSnapshot? operational, CancellationToken cancellationToken = default) => RequestAsync<PathRequest, FileEntryInfo>(peerId, "file.stat", new PathRequest(path, Operational: operational), cancellationToken);

    /// <summary>ディレクトリを件数を制限したページで読みます。</summary>
    /// <returns>一覧と継続位置です。</returns>
    public Task<FileListPage> ListAsync(string peerId, string path, string? continuation = null, CancellationToken cancellationToken = default) => RequestAsync<PathRequest, FileListPage>(peerId, "file.list", new PathRequest(path, continuation), cancellationToken);

    /// <summary>元入力の共通方針を一覧の全ページへ維持します。</summary>
    /// <returns>固定方針で取得したページです。</returns>
    public Task<FileListPage> ListAsync(string peerId, string path, string? continuation, ClusterOperationalSnapshot? operational, CancellationToken cancellationToken = default) => RequestAsync<PathRequest, FileListPage>(peerId, "file.list", new PathRequest(path, continuation, operational), cancellationToken);

    /// <summary>固定した内容版の32KiB以下を読みます。</summary>
    /// <returns>内容照合済みの分割データです。</returns>
    public Task<FileChunk> ReadChunkAsync(string peerId, FileChunkRequest request, CancellationToken cancellationToken = default) => RequestAsync<FileChunkRequest, FileChunk>(peerId, "file.readChunk", request, cancellationToken);

    /// <summary>同じ明示転送の退避先を開きます。</summary>
    /// <returns>退避先の識別子です。</returns>
    public Task<FileStageToken> OpenStageAsync(string peerId, FileStageRequest request, CancellationToken cancellationToken = default) => RequestAsync<FileStageRequest, FileStageToken>(peerId, "file.openStage", request, cancellationToken);

    /// <summary>転送対象の空ディレクトリも作成します。</summary>
    /// <returns>作成確認です。</returns>
    public Task StageDirectoryAsync(string peerId, FileStageDirectoryRequest request, CancellationToken cancellationToken = default) => RequestAsync<FileStageDirectoryRequest, bool>(peerId, "file.stageDirectory", request, cancellationToken);

    /// <summary>照合済みの分割データを書きます。</summary>
    /// <returns>書込み確認です。</returns>
    public Task WriteChunkAsync(string peerId, FileChunkWriteRequest request, CancellationToken cancellationToken = default) => RequestAsync<FileChunkWriteRequest, bool>(peerId, "file.writeChunk", request, cancellationToken);

    /// <summary>全内容を検証して宛先へ確定します。</summary>
    /// <returns>実際に確定した内容です。</returns>
    public Task<FileStageCommitResult> CommitStageAsync(string peerId, FileStageToken token, CancellationToken cancellationToken = default) => RequestAsync<FileStageToken, FileStageCommitResult>(peerId, "file.commitStage", token, cancellationToken);

    /// <summary>不要になった転送途中のデータを破棄します。</summary>
    /// <returns>破棄確認です。</returns>
    public Task DiscardStageAsync(string peerId, FileStageToken token, CancellationToken cancellationToken = default) => RequestAsync<FileStageToken, bool>(peerId, "file.discardStage", token, cancellationToken);

    /// <summary>場所と実体を固定して一つの有限要求を送ります。</summary>
    /// <returns>確認した応答です。</returns>
    private async Task<TResponse> RequestAsync<TRequest, TResponse>(string peerId, string kind, TRequest payload, CancellationToken cancellationToken = default)
    {
        PeerInfo? peer = cluster.GetPeers().FirstOrDefault(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal));
        if (peer is null)
        {
            throw new IOException("指定したPeerを確認できません。");
        }
        ClusterRequest request = new(Guid.NewGuid(), kind, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5), NodeValue.FromObject(payload));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Outcome != EOutcome.Succeeded || response.Value is null)
        {
            throw new IOException("分割ファイル操作の結果を確認できません。暗黙の再実行は行いません。");
        }
        return response.Value.Json.Deserialize<TResponse>() ?? throw new InvalidDataException("分割ファイル操作の応答型が一致しません。");
    }

    /// <summary>型ごとの操作を同じ受付境界へ登録します。</summary>
    private void Register<TRequest, TResponse>(string kind, Func<TRequest, CancellationToken, Task<TResponse>> handler)
    {
        cluster.RegisterHandler(kind, async (request, cancellation) =>
        {
            TRequest payload = request.Payload.Json.Deserialize<TRequest>() ?? throw new InvalidDataException("要求型が一致しません。");
            TResponse result = await handler(payload, cancellation).ConfigureAwait(false);
            return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(result));
        });
    }

    /// <summary>値のない操作でも確認済み応答を返します。</summary>
    private void RegisterVoid<TRequest>(string kind, Func<TRequest, CancellationToken, Task> handler)
    {
        Register<TRequest, bool>(kind, async (request, cancellation) =>
        {
            await handler(request, cancellation).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>ページ取得と単一パス照会に必要な範囲です。</summary>
    /// <param name="Path">明示パス。</param>
    /// <param name="Continuation">確認済みページの継続位置。</param>
    /// <param name="Operational">取得開始時に固定した共通運用版です。</param>
    private sealed record PathRequest(string Path, string? Continuation = null, ClusterOperationalSnapshot? Operational = null);
}
