using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;

/// <summary>固定NICと固定管理宛先で、停止するサービスの外から更新を配送します。</summary>
public sealed class UdpUpdatePeerTransport : IUpdatePeerTransport, IAsyncDisposable
{
    private readonly ClusterUpdateJob job;
    private readonly UpdateObserver observer;
    private readonly Dictionary<string, Guid> instances;
    private readonly string? clusterJobPath;

    /// <summary>送信したデータ量を通知します。受信側の最終検証は別途確認します。</summary>
    public Action<string, long, long>? TransferProgress
    {
        get; set;
    }

    /// <summary>開始時の宛先集合と明示通信場所を固定します。</summary>
    public UdpUpdatePeerTransport(ClusterUpdateJob job, string? clusterJobPath = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        this.job = ContractJson.Copy(job);
        this.clusterJobPath = clusterJobPath;
        instances = new(job.PeerInstances, StringComparer.Ordinal);
        observer = new(job.Binding, job.ClusterId);
    }

    /// <summary>現在のPeerが返す実RIDを副作用なしで確認します。</summary>
    /// <returns>明示したPeerの実RIDです。</returns>
    public Task<string> GetPlatformAsync(string peerId, CancellationToken cancellationToken = default)
    {
        return PostAsync<Guid, string>(peerId, "platform", job.Request.OperationId, cancellationToken);
    }

    /// <summary>更新ファイルを転送し、受信側で全ファイルのハッシュを検証します。</summary>
    /// <returns>実転送を完了した準備状態です。</returns>
    // 抑制理由: 各source FileStreamはusing宣言で全正常・例外経路を解放します。
    [SuppressMessage("Reliability", "CA2000", Justification = "ループ内sourceはusingで所有し解放します。")]
    public async Task<UpdatePeerResult> StageAsync(ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ReleaseVerifier.VerifyAsync(request.Manifest, request.PayloadRoot, cancellationToken).ConfigureAwait(false);
        UpdatePeerResult prepared = await PostAsync<ClusterUpdateRequest, UpdatePeerResult>(peerId, "prepare", request, cancellationToken).ConfigureAwait(false);
        if (prepared.Phase != EUpdatePhase.Transferring)
        {
            return prepared;
        }
        long total = request.Manifest.Files.Sum(file => file.Size);
        long transferred = 0;
        TransferProgress?.Invoke(peerId, transferred, total);
        if (await LocalUpdatePayload.CopyAsync(job, clusterJobPath, request, peerId, cancellationToken).ConfigureAwait(false)
            || await StreamAsync(request, peerId, cancellationToken).ConfigureAwait(false))
        {
            TransferProgress?.Invoke(peerId, total, total);
            return await PostAsync<Guid, UpdatePeerResult>(peerId, "seal", request.OperationId, cancellationToken).ConfigureAwait(false);
        }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
        foreach (ReleaseFile file in request.Manifest.Files)
        {
            string source = Path.Combine(request.PayloadRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            FileStageToken token = await PostAsync<UpdateFileRequest, FileStageToken>(peerId, "files/open", new UpdateFileRequest(request.OperationId, file.Path, job.CoordinatorPeerId, source), cancellationToken).ConfigureAwait(false);
            using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long offset = 0;
            do
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, 32768), cancellationToken).ConfigureAwait(false);
                ReadOnlyMemory<byte> chunk = buffer.AsMemory(0, count);
                FileChunkWriteRequest write = new(token, string.Empty, offset, chunk, Convert.ToHexString(SHA256.HashData(chunk.Span)).ToUpperInvariant(), file.Size, file.Sha256.ToUpperInvariant());
                await PostAsync<FileChunkWriteRequest, bool>(peerId, "files/write", write, cancellationToken).ConfigureAwait(false);
                offset += count;
                transferred += count;
                TransferProgress?.Invoke(peerId, transferred, total);
                if (count == 0)
                {
                    break;
                }
            }
            while (offset < file.Size);
            FileStageCommitResult committed = await PostAsync<FileStageToken, FileStageCommitResult>(peerId, "files/commit", token, cancellationToken).ConfigureAwait(false);
            string manifestHash = FileTransferManifest.ComputeSha256([new FileManifestEntry(string.Empty, false, file.Size, file.Sha256.ToUpperInvariant())]);
            if (committed.Bytes != file.Size || !string.Equals(committed.ManifestSha256, manifestHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("更新転送の最終内容が一致しません。");
            }
        }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        return await PostAsync<Guid, UpdatePeerResult>(peerId, "seal", request.OperationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>相手が対応する場合だけ連続送信し、結果不明時は照会へ切り替えます。</summary>
    /// <returns>新方式で全内容を保存できた場合はtrue、明示的な未対応だけfalseです。</returns>
    private async Task<bool> StreamAsync(ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default)
    {
        Contracts.Cluster.PeerInfo peer = await observer.FindAsync(peerId, instances[peerId], cancellationToken).ConfigureAwait(false);
        ManagementReply reply = await observer.SendAsync(peerId, peer.InstanceId, "updates.local.stream.capabilities", null, true, cancellationToken).ConfigureAwait(false);
        string missing = "Management.OperationMissing";
        string legacy = "Management.OperationModeMismatch";
        if (reply.Outcome == EOutcome.NotExecuted && (string.Equals(reply.Reason, missing, StringComparison.Ordinal) || string.Equals(reply.Reason, legacy, StringComparison.Ordinal)))
        {
            return false;
        }
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new UpdateTransportException("stream.capabilities", reply.Outcome);
        }
        UpdateStreamCapabilities capabilities = reply.Value.Json.Deserialize<UpdateStreamCapabilities>(ContractJson.Options) ?? throw new InvalidDataException("転送能力がありません。");
        if (capabilities.Version != 1)
        {
            return false;
        }
        if (!await UpdateStreamSender.CanConnectAsync(job.Binding, peer.Address, capabilities.Port, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        UpdateStreamTicket? ticket = await OpenStreamAsync(request.OperationId, peer, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return false;
        }
        try
        {
            await UpdateStreamSender.SendAsync(request, job.Binding, peer.Address, capabilities.Port, ticket,
                (bytes, total) => TransferProgress?.Invoke(peerId, bytes, total), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        // ACKの消失はペイロード再送の根拠にせず、同じトークンの完了状態だけを有限に確認します。
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        while (!limit.IsCancellationRequested)
        {
            UpdateStreamStatus status = await PostAsync<Guid, UpdateStreamStatus>(peerId, "stream/read", ticket.Token, limit.Token).ConfigureAwait(false);
            if (status.Token != ticket.Token || status.Reason is not null)
            {
                throw new UpdateTransportException("stream", EOutcome.Unknown);
            }
            if (status.Completed)
            {
                return true;
            }
            await Task.Delay(250, limit.Token).ConfigureAwait(false);
        }
        throw new UpdateTransportException("stream", EOutcome.Unknown);
    }

    /// <summary>実送信元へ許可を取り、旧版だけは同じIPの既存ピアを照合します。</summary>
    /// <returns>一回限りの許可、対応する受付がない場合はnullです。</returns>
    private async Task<UpdateStreamTicket?> OpenStreamAsync(Guid operationId, Contracts.Cluster.PeerInfo peer, CancellationToken cancellationToken = default)
    {
        ManagementReply reply = await observer.SendAsync(peer.PeerId, peer.InstanceId, "updates.local.stream.observer", UpdateObserver.Payload(operationId), false, cancellationToken).ConfigureAwait(false);
        string missing = "Management.OperationMissing";
        string legacy = "Management.OperationModeMismatch";
        if (reply.Outcome == EOutcome.NotExecuted && (string.Equals(reply.Reason, missing, StringComparison.Ordinal) || string.Equals(reply.Reason, legacy, StringComparison.Ordinal)))
        {
            string? source = observer.FindLocalPeerId();
            if (source is null)
            {
                return null;
            }
            return await PostAsync<UpdateStreamRequest, UpdateStreamTicket>(peer.PeerId, "stream/open", new(operationId, source), cancellationToken).ConfigureAwait(false);
        }
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new UpdateTransportException("stream.observer", reply.Outcome);
        }
        return reply.Value.Json.Deserialize<UpdateStreamTicket>(ContractJson.Options) ?? throw new InvalidDataException("更新転送の接続許可がありません。");
    }

    /// <summary>副作用要求を一度だけ送り、応答消失は照会に切り替えます。</summary>
    /// <returns>受理または結果未確認です。</returns>
    public async Task<UpdatePeerResult> DispatchAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await PostAsync<Guid, UpdatePeerResult>(peerId, "dispatch", operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Stopping, EOutcome.Unknown, Reason: "update.dispatchResponseMissing");
        }
    }

    /// <summary>再起動中の接続切断も副作用を再送せず照会だけに留めます。</summary>
    /// <returns>確認済み操作履歴、または照会待ちです。</returns>
    public async Task<UpdatePeerResult> ReadAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await PostAsync<Guid, UpdatePeerResult>(peerId, "read", operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Starting, EOutcome.Unknown, Reason: "update.peerTemporarilyUnavailable");
        }
    }

    /// <summary>確認済み成功Peerにのみ明示復元を要求します。</summary>
    /// <returns>別識別子を持つ復元操作です。</returns>
    public Task<UpdatePeerResult> RollbackAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        return PostAsync<Guid, UpdatePeerResult>(peerId, "rollback", operationId, cancellationToken);
    }

    /// <summary>不適合Peerの通常参加を明示的に止めます。</summary>
    /// <returns>除外要求の確認です。</returns>
    public async Task ExcludeAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await PostAsync<Guid, bool>(peerId, "exclude", job.Request.OperationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>明示した固定集合の通常実行を永続保留します。</summary>
    /// <returns>全Peerへの保留確認です。</returns>
    public async Task StopClusterAsync(Guid operationId, IReadOnlyList<string> peerIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peerIds);
        foreach (string peer in peerIds)
        {
            await PostAsync<Guid, bool>(peer, "hold", operationId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同一操作の保留だけを解除します。</summary>
    /// <returns>実解除の確認です。</returns>
    public async Task ReleaseAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        bool released = await PostAsync<Guid, bool>(peerId, "release", operationId, cancellationToken).ConfigureAwait(false);
        if (!released)
        {
            throw new InvalidDataException("更新保留の所有者を確認できません。");
        }
    }

    /// <summary>所有するUDP 監視処理だけを閉じます。</summary>
    /// <returns>監視終了です。</returns>
    ValueTask IAsyncDisposable.DisposeAsync() => ((IAsyncDisposable)observer).DisposeAsync();

    /// <summary>固定実体への操作を一度送り、再起動後は参照だけを新実体へ行います。</summary>
    /// <returns>共通管理応答から確認したドメイン結果です。</returns>
    private async Task<TResponse> PostAsync<TRequest, TResponse>(string peerId, string action, TRequest body, CancellationToken cancellationToken = default)
    {
        string read = "read";
        string platform = "platform";
        string streamRead = "stream/read";
        bool readOnly = string.Equals(action, read, StringComparison.Ordinal) || string.Equals(action, platform, StringComparison.Ordinal) || string.Equals(action, streamRead, StringComparison.Ordinal);
        Guid? expected = instances[peerId];
        if (string.Equals(action, read, StringComparison.Ordinal))
        {
            expected = null;
        }
        Contracts.Cluster.PeerInfo peer = await observer.FindAsync(peerId, expected, cancellationToken).ConfigureAwait(false);
        ManagementReply reply = await observer.SendAsync(peerId, peer.InstanceId, "updates.local." + action.Replace('/', '.'), UpdateObserver.Payload(body), readOnly, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new UpdateTransportException(action, reply.Outcome);
        }
        TResponse result = reply.Value.Json.Deserialize<TResponse>(ContractJson.Options) ?? throw new InvalidDataException("更新応答がありません。");
        if (result is UpdatePeerResult update && update.InstanceId == peer.InstanceId)
        {
            instances[peerId] = peer.InstanceId;
        }
        return result;
    }
}
