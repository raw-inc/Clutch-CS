using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>明示source PeerのローカルまたはRPC読取りを一つの入口にします。</summary>
internal sealed class FileTransferSource
{
    private readonly string peerId;
    private readonly bool local;
    private readonly FileTransferService service;
    private readonly IPeerFileTransfer? peers;
    private readonly ClusterOperationalSnapshot? operational;

    /// <summary>source Peerを固定します。</summary>
    internal FileTransferSource(string peerId, string localPeerId, FileTransferService service, IPeerFileTransfer? peers, ClusterOperationalSnapshot? operational)
    {
        this.peerId = peerId;
        local = string.Equals(peerId, localPeerId, StringComparison.Ordinal);
        this.service = service;
        this.peers = peers;
        this.operational = operational;
    }

    /// <summary>内容版を取得します。</summary>
    /// <returns>ファイルまたはディレクトリの同一性です。</returns>
    internal Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        if (local)
        {
            return service.StatAsync(path, operational?.Settings.FileConflictPolicy, cancellationToken);
        }
        return (peers ?? throw new InvalidOperationException("Peer転送を接続していません。")).StatAsync(peerId, path, operational, cancellationToken);
    }

    /// <summary>一階層を件数ごとに分けて取得します。</summary>
    /// <returns>一覧の一ページです。</returns>
    internal Task<FileListPage> ListAsync(string path, string? continuation, CancellationToken cancellationToken = default)
    {
        if (local)
        {
            return service.ListAsync(path, continuation, operational?.Settings.FileConflictPolicy, cancellationToken);
        }
        return (peers ?? throw new InvalidOperationException("Peer転送を接続していません。")).ListAsync(peerId, path, continuation, operational, cancellationToken);
    }

    /// <summary>内容版を固定して分割データを取得します。</summary>
    /// <returns>有限分割データです。</returns>
    internal Task<FileChunk> ReadAsync(FileChunkRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { Operational = operational };
        if (local)
        {
            return service.ReadChunkAsync(request, cancellationToken);
        }
        return (peers ?? throw new InvalidOperationException("Peer転送を接続していません。")).ReadChunkAsync(peerId, request, cancellationToken);
    }
}
