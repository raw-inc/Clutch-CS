using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>参加表示の残り時間からHTTP往復時間を差し引き、ブラウザー内でも計測を続けます。</summary>
internal static class PresenceResponseTiming
{
    /// <summary>Coreの日時をブラウザーの時計と比較せず、応答の残り表示時間を引き継ぎます。</summary>
    /// <returns>受信後も期限が減り続ける参加表示です。ほかの応答型は変更しません。</returns>
    internal static T? Receive<T>(T? value, Func<ObservationAge> elapsed)
    {
        if (value is IList<PresenceView> presence)
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
                    presence[index] = item with { ResponseAge = elapsed() };
                }
            }
        }
        return value;
    }
}
