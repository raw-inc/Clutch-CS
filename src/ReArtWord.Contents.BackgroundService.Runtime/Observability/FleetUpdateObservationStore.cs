using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>非永続の更新状況だけを有限保持し、通知停止を成功や完了へ読み替えません。</summary>
public sealed class FleetUpdateObservationStore(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> entries = [];

    /// <summary>短い通知停止を、進捗確認不能として示す期限です。</summary>
    public static TimeSpan Freshness => TimeSpan.FromSeconds(30);

    /// <summary>元の更新処理を開始・再送せず、より新しい表示情報だけを受け取ります。</summary>
    /// <returns>新しい表示を採用したかです。</returns>
    public bool Observe(FleetUpdateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Validate(observation);
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            Prune(now);
            if (entries.TryGetValue(observation.Activity.OperationId, out Entry? current) && !observation.IsNewerThan(current.Observation))
            {
                return false;
            }
            if (32 <= entries.Count && !entries.ContainsKey(observation.Activity.OperationId))
            {
                entries.Remove(entries.MinBy(pair => pair.Value.ReceivedAt).Key);
            }
            FleetUpdateObservation owned = observation with { Activity = observation.Activity with { Current = observation.Activity.Current.ToArray() } };
            entries[owned.Activity.OperationId] = new(owned, now);
            return true;
        }
    }

    /// <summary>完了済みは短期履歴、未完了の通知停止は確認不能として返します。</summary>
    /// <returns>最大32操作の現在観測です。</returns>
    public IReadOnlyList<FleetUpdateObservation> Read()
    {
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            Prune(now);
            return entries.Values.OrderByDescending(entry => entry.Observation.Activity.UpdatedAt)
                .Select(entry => entry.Observation with { Stale = entry.Observation.Stale || !entry.Observation.Activity.Completed && Freshness <= now - entry.ReceivedAt }).ToArray();
        }
    }

    /// <summary>無制限な対象数や不正な割合を表示データとして受理しません。</summary>
    private static void Validate(FleetUpdateObservation observation)
    {
        FleetUpdateActivity activity = observation.Activity;
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(activity.Current);
        ArgumentException.ThrowIfNullOrWhiteSpace(activity.ReleaseId);
        if (observation.PublisherId == Guid.Empty || observation.Sequence < 1 || activity.OperationId == Guid.Empty
            || 256 < activity.ReleaseId.Length || activity.Total is < 0 or > 65536
            || activity.Confirmed < 0 || activity.Total < activity.Confirmed || activity.Attention < 0 || activity.Total < activity.Attention
            || 32 < activity.Current.Count)
        {
            throw new ArgumentException("Invalid fleet update observation.", nameof(observation));
        }
        foreach (FleetUpdateActiveTarget target in activity.Current)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.ClusterId);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.PeerId);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.State);
            if (256 < target.ClusterId.Length || 256 < target.HostId.Length || 256 < target.PeerId.Length || 64 < target.State.Length)
            {
                throw new ArgumentException("Invalid fleet update target.", nameof(observation));
            }
        }
    }

    /// <summary>完了済みは5分、所有者を失った未完了は1時間まで有限保持します。</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (KeyValuePair<Guid, Entry> pair in entries.ToArray())
        {
            TimeSpan retention = TimeSpan.FromHours(1);
            if (pair.Value.Observation.Activity.Completed)
            {
                retention = TimeSpan.FromMinutes(5);
            }
            if (retention <= now - pair.Value.ReceivedAt)
            {
                entries.Remove(pair.Key);
            }
        }
    }

    /// <summary>各プロセスの時計で通知の鮮度を測定します。</summary>
    private sealed record Entry(FleetUpdateObservation Observation, DateTimeOffset ReceivedAt);
}
