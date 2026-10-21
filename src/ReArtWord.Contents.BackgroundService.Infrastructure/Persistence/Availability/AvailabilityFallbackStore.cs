using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>事前指定された候補保存だけを行い、汎用リポジトリの成功契約を変更しません。</summary>
internal sealed class AvailabilityFallbackStore(IDurableRepository journal, string directory, string peerId)
{
    private static string PointerScope => "peer:availability-fallback";

    /// <summary>採用pointerの実値とハッシュを確認し、primaryにない新規対象も列挙します。</summary>
    /// <returns>指定用途・明示媒体の有限な復旧対象です。</returns>
    internal async Task<IReadOnlyList<string>> ListActiveAsync(EStorageBackend? backend, EStoragePurpose purpose, int maximumItems, CancellationToken cancellationToken = default)
    {
        if (!backend.HasValue)
        {
            return [];
        }
        List<string> ids = [];
        IDurableRepository? store = null;
        foreach (DurableEntry entry in await journal.ListAsync(PointerScope, cancellationToken).ConfigureAwait(false))
        {
            AvailabilityFallbackPointer? pointer = entry.Value?.Json.Deserialize<AvailabilityFallbackPointer>(ContractJson.Options);
            if (pointer is null || !pointer.Active || pointer.Backend != backend.Value)
            {
                continue;
            }
            if (pointer.Slot is < 0 or > 1)
            {
                throw new InvalidDataException("代替候補の採用slotが不正です。");
            }
            store ??= Store(backend.Value);
            string slotKey = entry.Key + ":" + pointer.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            DurableEntry? saved = await store.ReadAsync("peer:availability:" + peerId, slotKey, cancellationToken).ConfigureAwait(false);
            AvailabilityFallbackRecord? record = saved?.Value?.Json.Deserialize<AvailabilityFallbackRecord>(ContractJson.Options);
            if (record is null || !string.Equals(ContractJson.Hash(record), pointer.Hash, StringComparison.Ordinal) || !string.Equals(Key(record.Purpose, record.DatasetId), entry.Key, StringComparison.Ordinal))
            {
                throw new InvalidDataException("採用済み代替候補の実値を確認できません。");
            }
            if (record.Purpose == purpose)
            {
                ids.Add(record.DatasetId);
                if (maximumItems < ids.Count)
                {
                    throw new InvalidDataException("代替復旧対象数が現在の明示容量を超えています。");
                }
            }
        }
        return ids;
    }

    /// <summary>保存と再読一致の後だけ候補の所在を永続記録します。</summary>
    /// <returns>実保存の確認完了です。</returns>
    internal async Task<AvailabilityFallbackPointer> SaveAsync(EStorageBackend backend, AvailabilityFallbackRecord record, CancellationToken cancellationToken = default)
    {
        IDurableRepository store = Store(backend);
        string scope = "peer:availability:" + peerId;
        string key = Key(record.Purpose, record.DatasetId);
        string hash = ContractJson.Hash(record);
        DurableEntry? activeEntry = await journal.ReadAsync(PointerScope, key, cancellationToken).ConfigureAwait(false);
        AvailabilityFallbackPointer? active = activeEntry?.Value?.Json.Deserialize<AvailabilityFallbackPointer>(ContractJson.Options);
        int slot = 0;
        if (active?.Slot == 0)
        {
            slot = 1;
        }
        string slotKey = key + ":" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EOutcome saved = await store.WriteLocalSnapshotAsync(scope, slotKey, NodeValue.FromObject(record), cancellationToken).ConfigureAwait(false);
        DurableEntry? confirmed = await store.ReadAsync(scope, slotKey, cancellationToken).ConfigureAwait(false);
        AvailabilityFallbackRecord? actual = confirmed?.Value?.Json.Deserialize<AvailabilityFallbackRecord>(ContractJson.Options);
        if (saved != EOutcome.Succeeded || actual is null || !string.Equals(ContractJson.Hash(actual), hash, StringComparison.Ordinal))
        {
            throw new IOException("明示した代替保存先への保存を確認できません。");
        }
        AvailabilityFallbackPointer pointer = new(backend, hash, false, slot);
        await PointerAsync(key + ":pending", pointer, cancellationToken).ConfigureAwait(false);
        return pointer;
    }

    /// <summary>有効化を確認した候補だけを再起動時の採用対象にします。</summary>
    /// <returns>採用選択の保存完了です。</returns>
    internal Task ActivateAsync(EStoragePurpose purpose, string datasetId, AvailabilityFallbackPointer pointer, CancellationToken cancellationToken = default) => PointerAsync(Key(purpose, datasetId), pointer with { Active = true }, cancellationToken);

    /// <summary>明示された媒体・ハッシュ・採用結果が揃った候補だけを読みます。</summary>
    /// <returns>現在方針と一致する採用済み候補です。</returns>
    internal async Task<AvailabilityFallbackRecord?> ReadActiveAsync(EStorageBackend backend, EStoragePurpose purpose, string datasetId, CancellationToken cancellationToken = default)
    {
        string key = Key(purpose, datasetId);
        DurableEntry? entry = await journal.ReadAsync(PointerScope, key, cancellationToken).ConfigureAwait(false);
        AvailabilityFallbackPointer? pointer = entry?.Value?.Json.Deserialize<AvailabilityFallbackPointer>(ContractJson.Options);
        if (pointer is null || !pointer.Active)
        {
            return null;
        }
        if (pointer.Backend != backend)
        {
            throw new InvalidDataException("代替保存の採用先が現在の明示方針と一致しません。");
        }
        DurableEntry? saved = await Store(backend).ReadAsync("peer:availability:" + peerId, key + ":" + pointer.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        AvailabilityFallbackRecord? record = saved?.Value?.Json.Deserialize<AvailabilityFallbackRecord>(ContractJson.Options);
        if (record is null || record.Purpose != purpose || !string.Equals(record.DatasetId, datasetId, StringComparison.Ordinal) || !string.Equals(ContractJson.Hash(record), pointer.Hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("確認済み代替候補の実値を復元できません。");
        }
        return record;
    }

    /// <summary>primaryの保存成功後は古い代替候補を自動復元しません。</summary>
    /// <returns>採用選択の解除完了です。</returns>
    internal async Task ClearAsync(EStoragePurpose purpose, string datasetId, CancellationToken cancellationToken = default)
    {
        EOutcome result = await journal.WriteLocalSnapshotAsync(PointerScope, Key(purpose, datasetId), NodeValue.Null, cancellationToken).ConfigureAwait(false);
        if (result != EOutcome.Succeeded)
        {
            throw new IOException("代替候補の採用解除を保存できません。");
        }
    }

    /// <summary>候補の実保存とは別に採用状態を永続化します。</summary>
    /// <returns>ローカルpointerの保存完了です。</returns>
    private async Task PointerAsync(string key, AvailabilityFallbackPointer pointer, CancellationToken cancellationToken = default)
    {
        if (await journal.WriteLocalSnapshotAsync(PointerScope, key, NodeValue.FromObject(pointer), cancellationToken).ConfigureAwait(false) != EOutcome.Succeeded)
        {
            throw new IOException("代替候補の所在を記録できません。");
        }
    }

    /// <summary>選択済み媒体だけを専用候補領域へ開きます。</summary>
    /// <returns>既存SQLiteまたはJSON実装です。</returns>
    private IDurableRepository Store(EStorageBackend backend)
    {
        string root = Path.Combine(directory, "availability-fallback");
        new FileAccessPolicy(new FileSystemOptions([directory])).ValidatePath(root, true);
        Directory.CreateDirectory(root);
        if (backend == EStorageBackend.Sqlite)
        {
            return new SqliteRepository(Path.Combine(root, "state.db"));
        }
        if (backend == EStorageBackend.Json)
        {
            return new JsonFileRepository(Path.Combine(root, "state.json"));
        }
        throw new ArgumentException("代替媒体が不正です。", nameof(backend));
    }

    /// <summary>任意のデータ名を一意なローカルキーへ符号化します。</summary>
    /// <returns>用途を含む固定キーです。</returns>
    private static string Key(EStoragePurpose purpose, string id) => ContractJson.Hash(new { purpose, id });
}
