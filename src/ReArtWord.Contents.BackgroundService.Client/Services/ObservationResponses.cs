using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>HTTP応答に含まれる経過時間へ通信時間を加え、ブラウザー内でも同じ計測を続けます。</summary>
internal static class ObservationResponses
{
    /// <summary>日時を表示用に残し、再読込みで完了や採取を新しい出来事にしません。</summary>
    /// <returns>この要求の経過時間を引き継いだ、同じ型の読取り結果です。</returns>
    internal static T? Receive<T>(T? value, Func<TimeSpan, ObservationAge> receive)
    {
        if (value is DashboardPeerSnapshot snapshot)
        {
            return (T)(object)Dashboard(snapshot, receive);
        }
        if (value is IList<DashboardPeerSnapshot> snapshots)
        {
            for (int index = 0; index < snapshots.Count; index++)
            {
                snapshots[index] = Dashboard(snapshots[index], receive);
            }
        }
        if (value is IList<PresenceView> presence)
        {
            ApplyPresence(presence, receive);
        }
        if (value is IList<NodePreviewSnapshot> previews)
        {
            for (int index = 0; index < previews.Count; index++)
            {
                NodePreviewSnapshot preview = previews[index];
                previews[index] = preview with { CaptureAge = Read(preview.CaptureAgeTicks, receive) };
            }
        }
        return value;
    }

    /// <summary>参加表示の残り時間は送信時点の値なので、応答待ちに費やした時間もブラウザーでの期限判定に含めます。</summary>
    private static void ApplyPresence(IList<PresenceView> presence, Func<TimeSpan, ObservationAge> receive)
    {
        for (int index = 0; index < presence.Count; index++)
        {
            PresenceView item = presence[index];
            if (item is null || item.Presence is null || item.RemainingTicks < 0 || item.Sequence is < 1 || item.Sequence is not null && item.PeerInstanceId == Guid.Empty)
            {
                throw new JsonException("参加表示の残り時間または通知順序が不正です。");
            }
            if (item.RemainingTicks is not null)
            {
                presence[index] = item with { ResponseAge = receive(TimeSpan.Zero) };
            }
        }
    }

    /// <summary>一括観測と単体観測に、同じ応答の通信時間を適用します。</summary>
    /// <returns>完了結果とノード診断の経過時間を復元した観測です。</returns>
    private static DashboardPeerSnapshot Dashboard(DashboardPeerSnapshot snapshot, Func<TimeSpan, ObservationAge> receive)
    {
        ExecutionHistoryPage? page = snapshot.Executions;
        if (page is not null)
        {
            page = page with { Entries = page.Entries.Select(view => view with { CompletionAge = Read(view.CompletionAgeTicks, receive) }).ToArray() };
        }
        return snapshot with
        {
            Executions = page,
            NodeExecutions = snapshot.NodeExecutions?.Select(node => node with { CompletionAge = Read(node.CompletionAgeTicks, receive) }).ToArray()
        };
    }

    /// <summary>不正な経過時間を、新しい観測として扱いません。</summary>
    /// <returns>旧形式ならnull、対応する応答なら日時に依存しない計測です。</returns>
    private static ObservationAge? Read(long? ticks, Func<TimeSpan, ObservationAge> receive)
    {
        if (ticks is null)
        {
            return null;
        }
        if (ticks < 0)
        {
            throw new JsonException("観測からの経過時間が不正です。");
        }
        return receive(TimeSpan.FromTicks(ticks.Value));
    }
}
