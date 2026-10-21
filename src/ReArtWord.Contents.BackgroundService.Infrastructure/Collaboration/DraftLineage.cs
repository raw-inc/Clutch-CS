using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>時刻に依存せず草稿の前後関係と有限の共通比較元を保持します。</summary>
internal sealed class DraftLineage
{
    private readonly Guid writer = Guid.NewGuid();
    private readonly Dictionary<Guid, long> clock = [];
    private readonly Queue<(DraftCheckpoint State, int Bytes)> history = [];
    private int historyBytes;

    /// <summary>同じ編集を含む、またはそれより新しい状態かを判定します。</summary>
    /// <returns>既知の編集に含まれるかです。</returns>
    internal bool Contains(IReadOnlyDictionary<Guid, long> other) => Contains(clock, other);

    /// <summary>受信候補が手元の編集をすべて含むかを判定します。</summary>
    /// <returns>手元の編集を含むかです。</returns>
    internal bool IsContainedBy(IReadOnlyDictionary<Guid, long> other) => Contains(other, clock);

    /// <summary>新しい編集前の状態を記録します。</summary>
    internal void Advance(DraftView before)
    {
        Remember(before);
        clock[writer] = clock.GetValueOrDefault(writer) + 1;
    }

    /// <summary>受信した後続版へ移る前の比較元を、架空のローカル編集を増やさず保持します。</summary>
    internal void Remember(DraftView before)
    {
        int bytes = JsonSerializer.SerializeToUtf8Bytes(before, ContractJson.Options).Length;
        history.Enqueue((new(new Dictionary<Guid, long>(clock), before), bytes));
        historyBytes += bytes;
    }

    /// <summary>両方が確認済みの最も新しい候補を比較元にします。</summary>
    /// <returns>共通候補、履歴外ならnullです。</returns>
    internal DraftView? Common(DraftAnnouncement other)
    {
        Dictionary<Guid, long> shared = clock.Where(pair => other.Clock!.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => Math.Min(pair.Value, other.Clock![pair.Key]));
        return history.Select(item => item.State).LastOrDefault(item => Contains(shared, item.Clock) && Contains(item.Clock, shared))?.Draft;
    }

    /// <summary>受信側も既知の編集と比較元を引き継ぎます。</summary>
    internal void Join(DraftAnnouncement other)
    {
        foreach ((Guid id, long version) in other.Clock!)
        {
            clock[id] = Math.Max(clock.GetValueOrDefault(id), version);
        }
    }

    /// <summary>比較履歴はローカルの容量内に留め、通信には版番号だけを添えます。</summary>
    /// <returns>本文の履歴を含めない通知です。</returns>
    internal DraftAnnouncement Announce(DraftAnnouncement state, int capacity, int maximumBytes)
    {
        while (history.Count != 0 && (capacity < history.Count || maximumBytes < historyBytes))
        {
            historyBytes -= history.Dequeue().Bytes;
        }
        return state with { Clock = new Dictionary<Guid, long>(clock) };
    }

    /// <summary>複数送信元の単調な版番号で古い通知を識別します。</summary>
    /// <returns>相手の全編集を確認済みかです。</returns>
    private static bool Contains(IReadOnlyDictionary<Guid, long> current, IReadOnlyDictionary<Guid, long> other)
        => other.All(pair => pair.Value <= current.GetValueOrDefault(pair.Key));
}
