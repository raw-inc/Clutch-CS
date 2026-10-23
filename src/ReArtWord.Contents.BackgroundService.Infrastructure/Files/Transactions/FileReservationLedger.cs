using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>現役予約だけを再利用するスロットへ置き、過去の全予約一覧を変更履歴へ複製しません。</summary>
internal sealed class FileReservationLedger(string scope, DurableEntry? header, IReadOnlyList<DurableEntry> slots, Dictionary<Guid, FileLease> active, bool slotted, long validatedAt = 0)
{
    private readonly long validated = validatedAt == 0 ? TimeProvider.System.GetTimestamp()
                                                       : validatedAt;

    /// <summary>競合判定に使う現役予約だけの集合です。</summary>
    internal Dictionary<Guid, FileLease> Active => active;

    /// <summary>同じガード行と予約スロットを原子的に更新する形式かです。</summary>
    internal bool Slotted => slotted;

    /// <summary>新規の空台帳を作らず、実在する旧一覧だけを移行対象にします。</summary>
    internal bool HasLegacyFormat => header?.Value is not null && !slotted;

    private string? HeaderVersion => header?.Version;

    /// <summary>原子的な全体ガードが同じ間だけ復号済み予約を再利用し、定期的に全スロットを再検証します。</summary>
    /// <returns>現在版として安全に再利用できるかです。</returns>
    internal async Task<bool> MatchesAsync(IDurableRepository repository, CancellationToken cancellationToken = default)
    {
        if (TimeSpan.FromSeconds(30) <= TimeProvider.System.GetElapsedTime(validated))
        {
            return false;
        }
        DurableEntry? current = await repository.ReadAsync(scope, "active", cancellationToken).ConfigureAwait(false);
        return string.Equals(header?.Version, current?.Version, StringComparison.Ordinal);
    }

    /// <summary>全参加者の保存を確認した一件だけを置換し、終了した予約の本文を保持し続けません。</summary>
    /// <returns>確認済み現在値です。不明な保存では再読取りします。</returns>
    internal FileReservationLedger? Updated(DurableReceipt receipt)
    {
        string key = "active", slotScope = scope + ":slots";
        DurableEntry? current = receipt.Entries.FirstOrDefault(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal)
            && string.Equals(entry.Key, key, StringComparison.Ordinal));
        if (receipt.Outcome != EOutcome.Succeeded || current?.Value is null)
        {
            return null;
        }
        Dictionary<string, DurableEntry> updatedSlots = slots.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        Dictionary<Guid, FileLease> updatedActive = new(active);
        foreach (DurableEntry entry in receipt.Entries.Where(entry => string.Equals(entry.Scope, slotScope, StringComparison.Ordinal)))
        {
            if (updatedSlots.GetValueOrDefault(entry.Key)?.Value is { } previous)
            {
                updatedActive.Remove(previous.Json.GetProperty("Id").GetGuid());
            }
            updatedSlots[entry.Key] = entry;
            if (entry.Value is not null)
            {
                FileLease lease = entry.Value.Json.Deserialize<FileLease>()!;
                updatedActive[lease.Id] = lease;
            }
        }
        bool usingSlots = current.Value.Json.TryGetProperty("Format", out JsonElement format) && format.GetInt32() == 2;
        if (!usingSlots)
        {
            updatedActive = current.Value.Json.Deserialize<Dictionary<Guid, FileLease>>() ?? [];
        }
        return new(scope, current, updatedSlots.Values.ToArray(), updatedActive, usingSlots, validated);
    }

    /// <summary>独立運転の予約を共有台帳へ混ぜません。</summary>
    /// <returns>予約の用途です。</returns>
    internal static string Scope(string peer, bool local) => local ? "peer:" + peer + ":file-reservations"
                                                                   : "file:reservations";

    /// <summary>旧一覧形式と、総実行件数によらず再利用する予約スロットの両方を読みます。</summary>
    /// <returns>一つのガード版で比較更新する予約候補です。</returns>
    internal static async Task<FileReservationLedger> ReadAsync(IDurableRepository repository, string scope, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            FileReservationLedger ledger = await ReadOnceAsync(repository, scope, cancellationToken).ConfigureAwait(false);
            DurableEntry? current = await repository.ReadAsync(scope, "active", cancellationToken).ConfigureAwait(false);
            if (string.Equals(ledger.HeaderVersion, current?.Version, StringComparison.Ordinal))
            {
                return ledger;
            }
        }
        throw new IOException("予約台帳の読取り中に版が変わりました。");
    }

    /// <summary>全体ガードを最後に照合する一回分の読取りです。</summary>
    /// <returns>比較更新前の候補です。</returns>
    private static async Task<FileReservationLedger> ReadOnceAsync(IDurableRepository repository, string scope, CancellationToken cancellationToken = default)
    {
        DurableEntry? header = await repository.ReadAsync(scope, "active", cancellationToken).ConfigureAwait(false);
        bool slotted = header?.Value?.Json.ValueKind == JsonValueKind.Object && header.Value.Json.TryGetProperty("Format", out JsonElement format)
            && format.ValueKind == JsonValueKind.Number && format.GetInt32() == 2;
        IReadOnlyList<DurableEntry> slots = await repository.ListAsync(scope + ":slots", cancellationToken).ConfigureAwait(false);
        if (!slotted)
        {
            // LEGACY-FORMAT: file-reservation-list-v1 — 全導入先の移行確認後に一覧形式の読取りを撤去します。
            return new(scope, header, slots, header?.Value?.Json.Deserialize<Dictionary<Guid, FileLease>>() ?? [], false);
        }
        Dictionary<Guid, FileLease> active = [];
        foreach (DurableEntry slot in slots)
        {
            if (slot.Value is null)
            {
                continue;
            }
            FileLease lease = slot.Value.Json.Deserialize<FileLease>() ?? throw new InvalidDataException("予約スロットの形式が不正です。");
            if (!active.TryAdd(lease.Id, lease))
            {
                throw new InvalidDataException("同じ予約が複数スロットへ保存されています。");
            }
        }
        return new(scope, header, slots, active, true);
    }

    /// <summary>全体ガードと一つの予約を同時に比較し、Coordinator交代でも包含パスの排他を崩しません。</summary>
    /// <returns>変更した一件、または旧形式との切替に必要な原子的変更です。</returns>
    internal IReadOnlyList<DurableMutation> Changes(Guid operation, FileLease? adding, Guid? removing, bool useSlots)
    {
        HashSet<Guid> removed = [];
        if (removing is Guid id)
        {
            removed.Add(id);
        }
        return ChangeSet(operation, adding, removed, useSlots);
    }

    /// <summary>所有確認済みの解放だけを同じガード版で保存し、保持中の予約を変更しません。</summary>
    /// <returns>全解放とガードの原子的な変更です。</returns>
    internal IReadOnlyList<DurableMutation> ReleaseChanges(Guid operation, IReadOnlySet<Guid> removing, bool useSlots)
        => ChangeSet(operation, null, removing, useSlots);

    /// <summary>新旧両形式で、取得または確認済み解放の集合だけを反映します。</summary>
    /// <returns>台帳の一回分の比較更新です。</returns>
    private List<DurableMutation> ChangeSet(Guid operation, FileLease? adding, IReadOnlySet<Guid> removing, bool useSlots)
    {
        Dictionary<Guid, FileLease> next = new(active);
        foreach (Guid removed in removing)
        {
            next.Remove(removed);
        }
        if (adding is not null)
        {
            next.Add(adding.Id, adding);
        }
        if (!useSlots)
        {
            List<DurableMutation> legacy = [new(scope, "active", header?.Version, NodeValue.FromObject(next))];
            legacy.AddRange(slots.Where(slot => slot.Value is not null).Select(slot => new DurableMutation(slot.Scope, slot.Key, slot.Version, null)));
            return legacy;
        }
        List<DurableMutation> changes = [new(scope, "active", header?.Version, NodeValue.FromObject(new { Format = 2, Change = operation }))];
        if (!slotted)
        {
            // 同じ取引で移すため、未解放の旧予約を消したり、取得し直したりしません。
            int index = 0;
            foreach (FileLease lease in next.Values)
            {
                string key = index.ToString(CultureInfo.InvariantCulture);
                DurableEntry? previous = slots.FirstOrDefault(slot => string.Equals(slot.Key, key, StringComparison.Ordinal));
                changes.Add(new(scope + ":slots", key, previous?.Version, NodeValue.FromObject(lease)));
                index++;
            }
            changes.AddRange(slots.Where(slot => slot.Value is not null && index <= int.Parse(slot.Key, CultureInfo.InvariantCulture))
                .Select(slot => new DurableMutation(slot.Scope, slot.Key, slot.Version, null)));
            return changes;
        }
        foreach (DurableEntry held in slots.Where(slot => slot.Value is not null && removing.Contains(slot.Value.Json.GetProperty("Id").GetGuid())))
        {
            changes.Add(new(held.Scope, held.Key, held.Version, null));
        }
        if (adding is not null)
        {
            DurableEntry? available = slots.FirstOrDefault(slot => slot.Value is null);
            string key = available?.Key ?? slots.Count.ToString(CultureInfo.InvariantCulture);
            changes.Add(new(scope + ":slots", key, available?.Version, NodeValue.FromObject(adding)));
        }
        return changes;
    }
}
