using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>元の実体と完全ハッシュを確認した作業ファイルだけを、一回のImportへ渡します。</summary>
internal sealed class SyncDownload
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly SyncExportStore files;
    private readonly ClusterOptions options;
    private readonly TimeProvider clock;
    private readonly Func<Runtime.Networking.LocalNetworkBinding> binding;

    /// <summary>既存UDPと既存の原子的Importを接続します。</summary>
    internal SyncDownload(PeerCluster cluster, IDurableRepository repository, SyncExportStore files, ClusterOptions options, TimeProvider clock,
        Func<Runtime.Networking.LocalNetworkBinding> binding)
    {
        this.cluster = cluster;
        this.repository = repository;
        this.files = files;
        this.options = options;
        this.clock = clock;
        this.binding = binding;
    }

    /// <summary>切断・期限・不完全な断片ではImportへ進みません。</summary>
    /// <returns>全体検証後の既存競合判定結果です。</returns>
    internal async Task<bool> ImportAsync(SyncManifest manifest, string sourcePeerId, Guid sourceInstanceId, bool allowConflict = false, CancellationToken cancellationToken = default)
    {
        Validate(manifest, sourcePeerId, sourceInstanceId, allowConflict);
        string path = files.TemporaryPath();
        using CancellationTokenSource timeout = new(manifest.ExpiresAt - clock.GetUtcNow(), clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long offset = 0;

            string address = cluster.GetPeers().Single(peer => string.Equals(peer.PeerId, sourcePeerId, StringComparison.Ordinal) && peer.InstanceId == sourceInstanceId).Address;
            if (await SyncStreamDownload.CopyAsync(manifest, binding(), address, stream,
                options.RequestTimeout, linked.Token).ConfigureAwait(false))
            {
                offset = manifest.Length;
            }

            // 旧版の送信側でも応答が一度に多数のUDP断片にならないよう、小分けに取得します。
            int maximumChunk = Math.Clamp((options.MaximumMessageBytes - 1024) / 2, 1, 8192);
            while (offset < manifest.Length)
            {
                Validate(manifest, sourcePeerId, sourceInstanceId, allowConflict);
                SyncChunk[] chunks = await ReadWindowAsync(manifest, sourcePeerId, sourceInstanceId, offset, maximumChunk, allowConflict, linked.Token).ConfigureAwait(false);
                foreach (SyncChunk chunk in chunks)
                {
                    await stream.WriteAsync(chunk.Data, linked.Token).ConfigureAwait(false);
                    offset += chunk.Data.Length;
                }
            }
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);
            stream.Position = 0;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, linked.Token).ConfigureAwait(false));
            if (stream.Length != manifest.Length || !string.Equals(hash, manifest.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncFailureException("Sync.HashMismatch");
            }
            stream.Position = 0;
            await files.Materialization.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                RepositorySnapshot snapshot = await JsonSerializer.DeserializeAsync<RepositorySnapshot>(stream, cancellationToken: linked.Token).ConfigureAwait(false)
                    ?? throw new SyncFailureException("Sync.InvalidSnapshot");
                Validate(manifest, sourcePeerId, sourceInstanceId, allowConflict);
                linked.Token.ThrowIfCancellationRequested();
                if (string.Equals(manifest.Phase, SyncManifest.HistoryPhase, StringComparison.Ordinal))
                {
                    RecoveryRepository recovery = RecoveryRepository.Create(repository) ?? throw new SyncFailureException("Sync.ProtocolMissing");
                    if (snapshot.Entries.Count != 0 || snapshot.Receipts.Count != 0 || snapshot.RecoveredHistory is null || snapshot.DeferredHistory is not null)
                    {
                        throw new SyncFailureException("Sync.InvalidSnapshot");
                    }
                    _ = await recovery.FillAsync(snapshot.RecoveredHistory, linked.Token).ConfigureAwait(false);
                    return true;
                }
                bool current = string.Equals(manifest.Phase, SyncManifest.CurrentPhase, StringComparison.Ordinal);
                if (snapshot.RecoveredHistory is not null || current != (snapshot.DeferredHistory is not null)
                    || current && !string.Equals(snapshot.DeferredSourcePeer, sourcePeerId, StringComparison.Ordinal))
                {
                    throw new SyncFailureException("Sync.InvalidSnapshot");
                }
                if (current && 0 < snapshot.PendingOperations?.Count)
                {
                    throw new SyncFailureException("Sync.PendingOperations");
                }
                return await ImportSnapshotAsync(snapshot, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                files.Materialization.Release();
            }
        }
        finally
        {
            SyncExportStore.RemoveFile(path);
        }
    }

    /// <summary>最大4断片の往復待ちを重ね、全応答を観測・検証して元の順序で渡します。</summary>
    /// <returns>最大32KiBに限定した、同じ固定スナップショットの連続した断片です。</returns>
    private async Task<SyncChunk[]> ReadWindowAsync(SyncManifest manifest, string peerId, Guid instanceId, long offset, int maximumChunk, bool allowConflict, CancellationToken cancellationToken = default)
    {
        List<Task<SyncResponse>> reads = [];
        for (long next = offset; next < manifest.Length && reads.Count < 4; next += maximumChunk)
        {
            int length = checked((int)Math.Min(maximumChunk, manifest.Length - next));
            reads.Add(ReadChunkAsync(manifest, peerId, instanceId, next, length, allowConflict, cancellationToken));
        }
        SyncResponse[] responses = await Task.WhenAll(reads).ConfigureAwait(false);
        SyncChunk[] chunks = new SyncChunk[responses.Length];
        for (int index = 0; index < responses.Length; index++)
        {
            SyncResponse response = responses[index];
            if (response.Reason is not null)
            {
                throw new SyncFailureException(response.Reason);
            }
            long expectedOffset = offset + (long)index * maximumChunk;
            int expectedLength = checked((int)Math.Min(maximumChunk, manifest.Length - expectedOffset));
            SyncChunk? chunk = response.Chunk;
            if (chunk is null || chunk.Id != manifest.Id || chunk.Offset != expectedOffset || chunk.Data is null || chunk.Data.Length != expectedLength)
            {
                throw new SyncFailureException("Sync.ChunkMismatch");
            }
            chunks[index] = chunk;
        }
        return chunks;
    }

    /// <summary>応答未確認だけ同じ固定断片をもう一度照会し、全書出しの作り直しを避けます。</summary>
    /// <returns>元のセッション期限内で確認した一断片の応答です。</returns>
    private async Task<SyncResponse> ReadChunkAsync(SyncManifest manifest, string peerId, Guid instanceId, long offset, int length, bool allowConflict, CancellationToken cancellationToken = default)
    {
        JsonElement request = JsonSerializer.SerializeToElement(new SyncReadRequest(manifest.Id, offset, length));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(manifest, peerId, instanceId, allowConflict);
            WireMessage? reply = await cluster.ExchangeAsync("sync.read", peerId, instanceId, request, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply.Body.Deserialize<SyncResponse>() ?? throw new SyncFailureException("Sync.ChunkUnconfirmed");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new SyncFailureException("Sync.ChunkUnconfirmed");
    }

    /// <summary>新しい分岐は隔離し、既に検証済みの同一候補で参加状態を往復させません。</summary>
    /// <returns>既存リポジトリの競合判定結果です。</returns>
    internal async Task<bool> ImportSnapshotAsync(RepositorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> before = await repository.GetConflictsAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        bool isolated = cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && peer.Status == EPeerStatus.Degraded);
        bool merged = await repository.ImportAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!merged)
        {
            IReadOnlyList<DurableEntry> after = await repository.GetConflictsAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            if (after.Count == 0)
            {
                // 別の取込みや明示解消が先に確定した場合、保存時点の判定で現在の参加状態を巻き戻しません。
                return true;
            }
            if (!isolated || before.Count == 0 || !string.Equals(ConflictVersions(before), ConflictVersions(after), StringComparison.Ordinal))
            {
                cluster.MarkSynchronizationConflict();
            }
        }
        return merged;
    }

    /// <summary>保存値の所有版から候補集合の同一性だけを確認します。</summary>
    /// <returns>候補集合の順序に依存しない識別値です。</returns>
    private static string ConflictVersions(IReadOnlyList<DurableEntry> entries) => ContractJson.Hash(entries.OrderBy(entry => entry.Scope, StringComparer.Ordinal).ThenBy(entry => entry.Key, StringComparer.Ordinal).ThenBy(entry => entry.Version, StringComparer.Ordinal).Select(entry => new { entry.Scope, entry.Key, entry.Version }).ToArray());

    /// <summary>マニフェストを別Peerや再起動後の実体に使い回しません。</summary>
    internal void Validate(SyncManifest manifest, string sourcePeerId, Guid sourceInstanceId, bool allowConflict = false)
    {
        if (manifest.Phase is not null && !string.Equals(manifest.Phase, SyncManifest.CurrentPhase, StringComparison.Ordinal)
            && !string.Equals(manifest.Phase, SyncManifest.HistoryPhase, StringComparison.Ordinal)
            || string.Equals(manifest.Phase, SyncManifest.HistoryPhase, StringComparison.Ordinal) && 67108864 < manifest.Length)
        {
            throw new SyncFailureException("Sync.ManifestMismatch");
        }
        if (manifest.Id == Guid.Empty || manifest.SourceInstanceId != sourceInstanceId || manifest.TargetInstanceId != cluster.LocalInstanceId
            || !string.Equals(manifest.SourcePeerId, sourcePeerId, StringComparison.Ordinal) || !string.Equals(manifest.TargetPeerId, cluster.LocalPeerId, StringComparison.Ordinal)
            || manifest.Length <= 0 || manifest.Hash is null || manifest.Hash.Length != 64 || !manifest.Hash.All(char.IsAsciiHexDigit))
        {
            throw new SyncFailureException("Sync.ManifestMismatch");
        }
        if (manifest.Stream is { } ticket && (ticket.Port is < 1 or > 65535 || ticket.Token == Guid.Empty))
        {
            throw new SyncFailureException("Sync.ManifestMismatch");
        }
        if (options.MaximumSyncSnapshotBytes < manifest.Length)
        {
            throw new SyncFailureException("Sync.SnapshotCapacity");
        }
        if (manifest.ExpiresAt <= clock.GetUtcNow() || clock.GetUtcNow() + TimeSpan.FromHours(1) < manifest.ExpiresAt)
        {
            throw new SyncFailureException("Sync.SessionExpired");
        }

        // 同期前の準備不足でもUnavailableになるため、読取同期はReadyを要求せずローカル観測の生存と元の実体を確認します。
        if (!cluster.GetPeers().Any(peer => string.Equals(peer.PeerId, sourcePeerId, StringComparison.Ordinal) && peer.InstanceId == sourceInstanceId
            && peer.Status != EPeerStatus.Detached && (allowConflict || peer.Status != EPeerStatus.Conflict)
            && clock.GetUtcNow() - peer.LastSeen <= options.PeerTimeout))
        {
            throw new SyncFailureException("Sync.SourceInstanceUnavailable");
        }
    }
}
