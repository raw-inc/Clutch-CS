using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>確認済み値は容量内で保持し、未保存候補を黙って追い出しません。</summary>
internal sealed class AvailabilityCache(AvailabilityOptions options)
{
    private readonly Lock sync = new();
    private readonly Dictionary<(EStoragePurpose Purpose, string Id), AvailabilityEntry> entries = [];
    private long bytes;

    /// <summary>対象の不変entryを取得します。</summary>
    /// <returns>未保持ならnullです。</returns>
    internal AvailabilityEntry? Get(EStoragePurpose purpose, string id)
    {
        using (sync.EnterScope())
        {
            return entries.GetValueOrDefault((purpose, id));
        }
    }

    /// <summary>保存確認本文だけを読み、未保存現在値を混ぜません。</summary>
    /// <returns>所有コピーと読取り専用の確認版メタデータです。</returns>
    internal AvailabilityValue? Confirmed(EStoragePurpose purpose, string id)
    {
        AvailabilityEntry? entry = Get(purpose, id);
        if (entry is null)
        {
            return null;
        }
        AvailabilityState state = entry.State with { EffectiveRevision = entry.State.SavedRevision, RecoveryRevision = entry.State.SavedRevision, IsUnsaved = false, IsLocalUnconfirmed = entry.State.IsLocalUnconfirmed && entry.Candidate is null, CanWrite = false, CandidateRevision = null, CandidateIsUnsaved = false };
        return new(entry.Confirmed?.Copy(), state.SavedRevision, state);
    }

    /// <summary>未保持と現在の所有コピーを区別します。</summary>
    /// <returns>現在の有効値、未保持ならnullです。</returns>
    internal AvailabilityValue? Current(EStoragePurpose purpose, string id)
    {
        AvailabilityEntry? entry = Get(purpose, id);
        if (entry is null)
        {
            return null;
        }
        return Current(entry);
    }

    /// <summary>全画面表示用に秘密値を含まない状態だけを返します。</summary>
    /// <returns>有限件数の状態一覧です。</returns>
    internal IReadOnlyList<AvailabilityState> States()
    {
        using (sync.EnterScope())
        {
            return entries.Values.Select(entry => entry.State).ToArray();
        }
    }

    /// <summary>件数とバイト上限を満たす場合だけ候補を保持します。</summary>
    /// <returns>保持できたかです。</returns>
    internal bool Put(AvailabilityEntry entry)
    {
        long size = Size(entry);
        if (options.MaximumBytes < size || 1024 < entry.State.DatasetId.Length)
        {
            return false;
        }
        using (sync.EnterScope())
        {
            (EStoragePurpose, string) key = (entry.State.Purpose, entry.State.DatasetId);
            AvailabilityEntry? old = entries.GetValueOrDefault(key);
            long oldSize = 0;
            int additional = 1;
            if (old is not null)
            {
                oldSize = Size(old);
                additional = 0;
            }
            foreach ((EStoragePurpose Purpose, string Id) removable in entries.Where(pair => pair.Key != key && pair.Value.State.CandidateRevision is null).Select(pair => pair.Key).ToArray())
            {
                if (entries.Count + additional <= options.MaximumItems && bytes - oldSize + size <= options.MaximumBytes)
                {
                    break;
                }
                bytes -= Size(entries[removable]);
                entries.Remove(removable);
            }
            if (options.MaximumItems < entries.Count + additional || options.MaximumBytes < bytes - oldSize + size)
            {
                return false;
            }
            entries[key] = entry;
            bytes += size - oldSize;
            return true;
        }
    }

    /// <summary>現在値を必要な所有コピーとして返します。</summary>
    /// <returns>未採用候補は現在値へ紛れ込ませません。</returns>
    internal static AvailabilityValue Current(AvailabilityEntry entry)
    {
        return new(entry.Effective?.Copy(), entry.State.EffectiveRevision, entry.State);
    }

    /// <summary>実採用後に旧未保存版を残さず、保持できない本文を明示します。</summary>
    /// <returns>本文をキャッシュしない新しい確定メタデータです。</returns>
    internal static AvailabilityEntry WithoutConfirmedBody(AvailabilityEntry entry, bool adopted)
    {
        AvailabilityEntry bounded = entry with { Confirmed = null, State = entry.State with { CanRead = false, CanWrite = false, Reason = "Availability.CacheCapacity" } };
        if (adopted)
        {
            bounded = bounded with { Effective = null, Candidate = null };
        }
        return bounded;
    }

    /// <summary>保持値と識別子のバイト数を計算します。</summary>
    /// <returns>値とメタデータを含む上限判定値です。</returns>
    private static long Size(AvailabilityEntry entry) => ValueBytes(entry.Confirmed) + Math.Max(ValueBytes(entry.Effective), ValueBytes(entry.Candidate)) * 2 + Encoding.UTF8.GetByteCount(entry.State.DatasetId) + 512L;

    /// <summary>バイナリとJSONの実保存表現を計数します。</summary>
    /// <returns>値のバイト数です。</returns>
    private static long ValueBytes(NodeValue? value)
    {
        if (value is null)
        {
            return 0;
        }
        return value.PayloadSize();
    }
}
