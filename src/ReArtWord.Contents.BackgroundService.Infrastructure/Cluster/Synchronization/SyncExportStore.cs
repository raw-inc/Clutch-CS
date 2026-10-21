using System.Collections.Concurrent;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>固定書出しをローカルファイルへ保存し、受信元を限定した読み出しだけを提供します。</summary>
internal sealed class SyncExportStore : IAsyncDisposable
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly ClusterOptions options;
    private readonly TimeProvider clock;
    private readonly Func<Runtime.Networking.LocalNetworkBinding> binding;
    private readonly string root = Path.Combine(Path.GetTempPath(), "Contents.RepositorySync", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<Guid, Export> exports = [];
    private readonly SemaphoreSlim slots;
    private readonly Lock admission = new();

    /// <summary>完全本文の復元は同時に一つとし、ファイル転送の並行数とは分離します。</summary>
    internal SemaphoreSlim Materialization
    {
        get;
    } = new(1, 1);

    /// <summary>ローカルの作業容量と固定期限を指定します。</summary>
    internal SyncExportStore(PeerCluster cluster, IDurableRepository repository, ClusterOptions options, TimeProvider clock,
        Func<Runtime.Networking.LocalNetworkBinding> binding)
    {
        this.cluster = cluster;
        this.repository = repository;
        this.options = options;
        this.clock = clock;
        this.binding = binding;
        slots = new(options.MaximumSyncSessions, options.MaximumSyncSessions);
    }

    /// <summary>まだ提示していない固定書出しの区分を、同じ実体の一回の照合へ渡します。</summary>
    /// <returns>元スナップショットの完全な区分指紋です。</returns>
    internal SyncInventoryRequest Describe(SyncManifest manifest)
    {
        Export export = Find(manifest);
        SyncInventory.Validate(export.Hashes);
        HistoryRetentionBoundary? boundary = export.Metadata.HistoryBoundary;
        if (boundary is not null)
        {
            boundary = ContractJson.Copy(boundary);
        }
        return new(manifest.Id, export.Hashes.ToArray(), manifest.ExpiresAt, boundary);
    }

    /// <summary>全本文を読み直さず、保持境界を理解する相手が必要かを確認します。</summary>
    /// <returns>固定版に履歴の保持境界が含まれるかです。</returns>
    internal bool HasHistoryBoundary(SyncManifest manifest) => Find(manifest).Metadata.HistoryBoundary is not null;

    /// <summary>元期限と一枠の所有権を維持したまま、未提示の書出しを差分へ絞ります。</summary>
    /// <returns>元の区分指紋と、実際に転送する差分の契約です。</returns>
    internal async Task<SyncInventoryOffer> NarrowAsync(SyncManifest manifest, IReadOnlyList<string> knownHashes, CancellationToken cancellationToken = default)
    {
        SyncInventory.Validate(knownHashes);
        await Materialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? path = null;
        try
        {
            Export source = Find(manifest);
            string[] hashes = source.Hashes;
            SyncInventory.Validate(hashes);
            path = TemporaryPath();
            SyncManifest narrowed;
            SyncSnapshotIndex index;
            using (SyncSnapshotStream stream = new(path, options.MaximumSyncSnapshotBytes))
            {
                using FileStream input = new(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536);
                index = SyncSnapshotWriter.Copy(input, stream, source.Index, hashes, knownHashes, cancellationToken);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                narrowed = manifest with { Length = stream.Length, Hash = hash };
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (admission.EnterScope())
            {
                Export original = Get(manifest.Id, manifest.TargetPeerId, manifest.TargetInstanceId);
                if (original.Manifest != manifest)
                {
                    throw new SyncFailureException("Sync.ManifestMismatch");
                }
                File.Move(path, original.Path, true);
                exports[manifest.Id] = original with { Manifest = narrowed, Index = index };
            }
            return new(hashes.ToArray(), narrowed);
        }
        finally
        {
            if (path is not null)
            {
                RemoveFile(path);
            }
            Materialization.Release();
        }
    }

    /// <summary>同じ対象へ既に所有している逆方向書出しだけを参照します。</summary>
    /// <returns>期限内の元マニフェストです。</returns>
    internal SyncManifest Manifest(Guid id, string peerId, Guid instanceId) => Get(id, peerId, instanceId).Manifest;

    /// <summary>同じ元ファイルに結び付いた指紋だけを使用します。</summary>
    /// <returns>実体・期限・マニフェストを検証した所有情報です。</returns>
    private Export Find(SyncManifest manifest)
    {
        Export export = Get(manifest.Id, manifest.TargetPeerId, manifest.TargetInstanceId);
        if (export.Manifest != manifest)
        {
            throw new SyncFailureException("Sync.ManifestMismatch");
        }
        return export;
    }

    /// <summary>相手の指紋が既知なら、全体の書出しと再読込みを挟まず差分だけを固定します。</summary>
    /// <returns>元の全区分指紋と、同じスナップショットから選んだ転送内容です。</returns>
    internal async Task<SyncInventoryOffer> CreateOfferAsync(PeerInfo target, SyncInventoryRequest known, CancellationToken cancellationToken = default)
    {
        SyncInventory.Validate(known.Hashes);
        SyncManifest manifest = await CreateCoreAsync(target, true, known, null, cancellationToken: cancellationToken).ConfigureAwait(false);
        Export source = Find(manifest);
        return new(source.Hashes.ToArray(), manifest, Full: !SyncInventory.Overlaps(source.Metadata.HistoryBoundary, known.Boundary));
    }

    /// <summary>相手の指紋が未取得の場合は、従来と同じ完全な固定書出しを用意します。</summary>
    /// <returns>同じバイト列を参照する元マニフェストです。</returns>
    internal Task<SyncManifest> CreateAsync(PeerInfo target, bool inventory, CancellationToken cancellationToken = default)
        => CreateCoreAsync(target, inventory, null, null, cancellationToken: cancellationToken);

    /// <summary>先行状態または一バッチの履歴を、既存TCPと断片フォールバックの同じ固定転送へ載せます。</summary>
    /// <returns>元の取引で固定された本文のマニフェストです。</returns>
    internal Task<SyncManifest> CreatePayloadAsync(PeerInfo target, RepositorySnapshot snapshot, string phase, CancellationToken cancellationToken = default)
        => CreateCoreAsync(target, false, null, null, snapshot, phase, cancellationToken);

    /// <summary>対応媒体は本文をファイルへ書く前に指紋を固定し、相手との照合に備えます。</summary>
    /// <returns>一回だけ本文作成へ使用する準備、または従来経路を使うnullです。</returns>
    internal async Task<SyncPreparedExport?> PrepareAsync(PeerInfo target, CancellationToken cancellationToken = default)
    {
        await CleanupAsync(cancellationToken).ConfigureAwait(false);
        return await SyncPreparedExport.CreateAsync(this, target, repository, slots, Materialization, options, clock, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>準備時の現在値と履歴だけから、必要な区分の本文を初めて作成します。</summary>
    /// <returns>元の照合ID・期限を保った固定ファイルです。</returns>
    internal Task<SyncManifest> MaterializeAsync(SyncPreparedExport prepared, IReadOnlyList<string>? known, CancellationToken cancellationToken = default)
        => CreateCoreAsync(prepared.Target, true, known is null ? null
                                                                : prepared.Original with { Hashes = known }, prepared, cancellationToken: cancellationToken);

    /// <summary>相手へ提示する前に、完全な書出しの長さとハッシュを確定します。</summary>
    /// <returns>同じバイト列を参照する固定マニフェストです。</returns>
    // 抑制理由: 接続の所有権をexportsへ移し、期限・明示終了・Store終了のすべてで非同期回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "作成した接続をExportで所有します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "作成した接続の所有権をExportへ渡します。")]
    private async Task<SyncManifest> CreateCoreAsync(PeerInfo target, bool inventory, SyncInventoryRequest? known, SyncPreparedExport? prepared,
        RepositorySnapshot? supplied = null, string? phase = null, CancellationToken cancellationToken = default)
    {
        await CleanupAsync(cancellationToken).ConfigureAwait(false);
        if (known is not null)
        {
            SyncInventory.Validate(known.Hashes);
        }
        prepared?.Begin(this, clock.GetUtcNow());
        if (prepared is null && !await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SyncFailureException("Sync.ExportCapacity");
        }
        string? path = null;
        bool retained = false;
        bool materializing = false;
        try
        {
            await Materialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            materializing = true;
            path = TemporaryPath();
            using SyncSnapshotStream stream = new(path, options.MaximumSyncSnapshotBytes);
            (string[] hashes, SyncSnapshotIndex index) = await SyncSnapshotWriter.WriteSourceAsync(stream, repository, prepared, supplied, inventory, known, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (options.MaximumSyncSnapshotBytes < stream.Length)
            {
                throw new SyncFailureException("Sync.SnapshotCapacity");
            }
            stream.Position = 0;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset expires = prepared?.Original.ExpiresAt ?? clock.GetUtcNow() + options.SyncSessionLifetime;
            if (expires <= clock.GetUtcNow())
            {
                throw new SyncFailureException("Sync.SessionExpired");
            }
            SyncManifest Publish()
            {
                Guid id = prepared?.Original.Id ?? Guid.NewGuid();
                SyncExportStream transfer = new(binding(), target, id, path, expires - clock.GetUtcNow(), clock);
                SyncManifest manifest = new(id, cluster.LocalPeerId, cluster.LocalInstanceId, target.PeerId, target.InstanceId, stream.Length, hash, expires, transfer.Ticket, phase);
                exports[manifest.Id] = new(path, manifest, transfer, hashes, index);
                return manifest;
            }
            SyncManifest manifest = prepared is null ? Publish()
                                                     : prepared.Publish(Publish);
            retained = true;
            return manifest;
        }
        finally
        {
            if (materializing)
            {
                Materialization.Release();
            }
            if (!retained)
            {
                if (path is not null)
                {
                    RemoveFile(path);
                }
                if (prepared is null)
                {
                    slots.Release();
                }
            }
        }
    }

    /// <summary>期限と元の対象実体を確認して、指定範囲だけを読みます。</summary>
    /// <returns>同じ固定ファイルの断片です。</returns>
    internal async Task<SyncChunk> ReadAsync(SyncReadRequest request, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        Export export = Get(request.Id, peerId, instanceId);
        if (request.Offset < 0 || request.Length is < 1 or > 65536 || export.Manifest.Length <= request.Offset)
        {
            throw new SyncFailureException("Sync.Range");
        }
        int length = checked((int)Math.Min(request.Length, export.Manifest.Length - request.Offset));
        byte[] bytes = new byte[length];
        using FileStream stream = new(export.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
        stream.Position = request.Offset;
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new(request.Id, request.Offset, bytes);
    }

    /// <summary>元の対象だけが受取後に早期解放できます。</summary>
    /// <returns>所有接続と固定ファイルの解放です。</returns>
    internal Task CloseAsync(Guid id, string peerId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        _ = Get(id, peerId, instanceId);
        return RemoveAsync(id, cancellationToken);
    }

    /// <summary>自分が開始した転送の終了時に、自分の書出しだけを解放します。</summary>
    /// <returns>所有接続と固定ファイルの解放です。</returns>
    internal Task ReleaseAsync(Guid id, CancellationToken cancellationToken = default) => RemoveAsync(id, cancellationToken);

    /// <summary>自分が生成したランダム名以外を作業パスに使いません。</summary>
    /// <returns>この所有root内の新しいファイル名です。</returns>
    internal string TemporaryPath()
    {
        Directory.CreateDirectory(root);
        return Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
    }

    /// <summary>元期限を過ぎた書出しを削除し、再送で延長しません。</summary>
    /// <returns>期限切れ接続の終了確認です。</returns>
    internal async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        foreach (Export export in exports.Values.Where(item => item.Manifest.ExpiresAt <= clock.GetUtcNow()))
        {
            await RemoveAsync(export.Manifest.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>受信元のすり替えや期限切れを拒否します。</summary>
    /// <returns>確認できた所有書出しです。</returns>
    private Export Get(Guid id, string peerId, Guid instanceId)
    {
        if (!exports.TryGetValue(id, out Export? export) || export.Manifest.ExpiresAt <= clock.GetUtcNow()
            || !string.Equals(export.Manifest.TargetPeerId, peerId, StringComparison.Ordinal) || export.Manifest.TargetInstanceId != instanceId)
        {
            throw new SyncFailureException("Sync.SessionUnavailable");
        }
        return export;
    }

    /// <summary>ローカル書出しと同時数予約を一度だけ解放します。</summary>
    /// <returns>接続終了後の枠とファイルの解放です。</returns>
    private async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Export? removed;
        using (admission.EnterScope())
        {
            if (!exports.TryRemove(id, out removed))
            {
                return;
            }
        }
        try
        {
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = removed.Transfer.ConfigureAwait(false);
        }
        finally
        {
            RemoveFile(removed.Path);
            slots.Release();
        }
    }

    /// <summary>作業ファイルの後片付けは共有状態を変更しません。</summary>
    internal static void RemoveFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // OSがファイルを保持する場合でも、当該セッションは再利用しません。
        }
    }

    /// <summary>通信終了後に、この所有rootのファイルだけを解放します。</summary>
    /// <returns>すべての所有接続と作業ファイルの解放です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        foreach (Guid id in exports.Keys)
        {
            await RemoveAsync(id).ConfigureAwait(false);
        }
        slots.Dispose();
        Materialization.Dispose();
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 未解放のファイルがある場合、他のrootを再帰削除しません。
        }
    }

    /// <summary>固定ファイルとマニフェストの一対です。</summary>
    private sealed record Export(string Path, SyncManifest Manifest, SyncExportStream Transfer, string[] Hashes, SyncSnapshotIndex Index)
    {
        /// <summary>本文を含まない保持境界です。</summary>
        internal RepositorySnapshot Metadata => Index.Metadata;
    }
}
