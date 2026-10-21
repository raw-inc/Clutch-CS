using System.Globalization;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Client.Overview;

/// <summary>通常稼働の確認を維持しながら、本文の背景補完を件数の割合で表示します。</summary>
// 抑制理由: Razorの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開宣言に合わせます。")]
public sealed partial class PeerStateLabel
{
    /// <summary>今回確認した稼働状態です。未確認・同期中をReadyへ置き換えません。</summary>
    [Parameter] public string State
    {
        get;
        set;
    } = "Unconfirmed";

    /// <summary>同じCore実体から取得した、通常参加とは独立した補完件数です。</summary>
    [Parameter] public HistorySynchronizationStatus? History
    {
        get;
        set;
    }

    private string Percent => Math.Floor(History?.ProgressPercent ?? 0).ToString("0", CultureInfo.InvariantCulture);

    private string Description
    {
        get
        {
            string text = Ui.Format("History verified: {0}/{1}. Workflows remain available.", History?.Completed ?? 0, History?.Total ?? 0);
            if (History?.Problem is { } problem)
            {
                text += " · " + problem;
            }
            return text;
        }
    }
}
