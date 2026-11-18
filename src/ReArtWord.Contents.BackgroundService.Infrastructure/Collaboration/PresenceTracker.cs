using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>参加表示の順序と寿命を管理し、草稿の内容や編集版には触れません。</summary>
internal sealed class PresenceTracker(IPeerDirectory peers, TimeProvider clock, CollaborationOptions options)
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Peer, Guid Instance, Guid Ui, string Workflow), PresenceView> entries = [];
    private readonly Dictionary<(string Peer, Guid Instance), long> retired = [];
    private long sequence;

    /// <summary>同じ内容の在席維持も、新たに受け付けた一回として採番します。</summary>
    /// <returns>送信元Core実体の寿命に属する一回の通知です。</returns>
    internal PresenceAnnouncement Create(PresenceUpdate update) => new(ContractJson.Copy(update), Interlocked.Increment(ref sequence), options.PresenceLifetime.Ticks);

    /// <summary>同じ送信元の重複・古い通知で、表示期限や座標を戻しません。</summary>
    internal void Receive(string peer, Guid instance, PresenceAnnouncement announcement)
    {
        if (announcement.Sequence < 1 || announcement.LifetimeTicks < 1)
        {
            throw new ArgumentException("参加通知の連番または有効期間が不正です。", nameof(announcement));
        }
        using (gate.EnterScope())
        {
            Prune();
            if (!IsCurrent(peer, instance))
            {
                return;
            }
            (string, Guid) source = (peer, instance);
            (string, Guid, Guid, string) key = (peer, instance, announcement.Presence.UiInstanceId, announcement.Presence.WorkflowId);
            if (announcement.Sequence <= retired.GetValueOrDefault(source)
                || entries.TryGetValue(key, out PresenceView? previous) && announcement.Sequence <= previous.Sequence)
            {
                return;
            }
            retired.TryAdd(source, 0);
            TimeSpan lifetime = TimeSpan.FromTicks(Math.Min(options.PresenceLifetime.Ticks, announcement.LifetimeTicks));
            entries[key] = new(peer, instance, ContractJson.Copy(announcement.Presence), clock.GetUtcNow() + lifetime)
            { Sequence = announcement.Sequence, RemainingTicks = lifetime.Ticks, ResponseAge = new(clock) };
            Trim();
        }
    }

    /// <summary>新形式を受信済みのCoreから届く互換通知は無視し、二重に在席期間を更新しません。</summary>
    internal void ReceiveLegacy(string peer, Guid instance, PresenceUpdate update)
    {
        using (gate.EnterScope())
        {
            Prune();
            if (retired.ContainsKey((peer, instance)) || !IsCurrent(peer, instance))
            {
                return;
            }
            entries[(peer, instance, update.UiInstanceId, update.WorkflowId)] = new(peer, instance, ContractJson.Copy(update), clock.GetUtcNow() + options.PresenceLifetime)
            { RemainingTicks = options.PresenceLifetime.Ticks, ResponseAge = new(clock) };
            Trim();
        }
    }

    /// <summary>表示の残り時間を応答へ渡し、同じ通知の再取得を新たな参加にしません。</summary>
    /// <returns>現在のUI別表示です。呼出元が内部の配列を書き換えることはできません。</returns>
    internal IReadOnlyList<PresenceView> Read(string workflow)
    {
        using (gate.EnterScope())
        {
            Prune();
            return entries.Values.Where(item => string.Equals(item.Presence.WorkflowId, workflow, StringComparison.Ordinal))
                .Select(item => ContractJson.Copy(item with { RemainingTicks = item.Remaining!.Value.Ticks }) with { ResponseAge = new(clock) }).ToArray();
        }
    }

    /// <summary>再起動前の実体から遅れて届いた通知を受け入れません。</summary>
    /// <returns>このCore、または現在発見している同じ実体かです。</returns>
    private bool IsCurrent(string peer, Guid instance) => peers.GetPeers().Any(item => item.InstanceId == instance && string.Equals(item.PeerId, peer, StringComparison.Ordinal));

    /// <summary>表示を回収しても、その送信元が同じ実体の間は古い連番を再受付しません。</summary>
    private void Prune()
    {
        HashSet<(string Peer, Guid Instance)> current = peers.GetPeers().Select(item => (item.PeerId, item.InstanceId)).ToHashSet();
        foreach ((string Peer, Guid Instance, Guid Ui, string Workflow) key in entries.Keys.ToArray())
        {
            if (!current.Contains((key.Peer, key.Instance)) || entries[key].Remaining <= TimeSpan.Zero)
            {
                Retire(key);
            }
        }
        foreach ((string Peer, Guid Instance) key in retired.Keys.Where(key => !current.Contains(key)).ToArray())
        {
            retired.Remove(key);
        }
    }

    /// <summary>表示の容量制限で外した通知も、同じ連番で復活させません。</summary>
    private void Trim()
    {
        while (options.MaximumPresence < entries.Count)
        {
            Retire(entries.MinBy(item => item.Value.Remaining).Key);
        }
    }

    /// <summary>表示本文を解放し、再送拒否に必要な送信元の連番だけ残します。</summary>
    private void Retire((string Peer, Guid Instance, Guid Ui, string Workflow) key)
    {
        if (entries.Remove(key, out PresenceView? removed) && removed.Sequence is { } prior)
        {
            (string, Guid) source = (key.Peer, key.Instance);
            retired[source] = Math.Max(retired.GetValueOrDefault(source), prior);
        }
    }
}
