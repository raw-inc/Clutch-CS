using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;

/// <summary>同じCore実体から取得した有限の実測系列です。</summary>
// 抑制理由: 公開RazorコンポーネントのParameter型として同じ観測モデルを共有します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "公開Razor Parameterに必要な観測データ型です。")]
public sealed record OverviewPeer(PeerInfo Peer, IReadOnlyList<ResourceSample> Samples, string? Problem = null, EPeerStatus? VerifiedStatus = null,
    DateTimeOffset? StatusCheckedAt = null, HistorySynchronizationStatus? HistorySynchronization = null)
{
    /// <summary>同一Hostを複数クラスタから集約するときの照会先です。</summary>
    public string? ClusterId
    {
        get;
        init;
    }

    /// <summary>最後の記録を表示するだけで、現在の稼働確認ではありません。</summary>
    public bool Offline
    {
        get;
        init;
    }

    /// <summary>最後に確認した実測値です。</summary>
    internal ResourceSample? Latest
    {
        get
        {
            if (Samples.Count == 0)
            {
                return null;
            }
            return Samples[^1];
        }
    }
}
