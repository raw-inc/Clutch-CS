using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Charts;

/// <summary>稼働中の円形アクセスランプを、同じ速度・対象ごとの位相で表示します。</summary>
// 抑制理由: Razorの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開宣言に合わせます。")]
public sealed partial class ActivityRing
{
    /// <summary>現在の動作が確認できるときに点灯を回します。</summary>
    [Parameter] public bool Active
    {
        get;
        set;
    }

    /// <summary>対象を切り替えても安定した位相差を付ける識別子です。</summary>
    [Parameter] public string Identity
    {
        get;
        set;
    } = string.Empty;

    private string Delay
    {
        get
        {
            uint hash = 2166136261;
            foreach (char character in Identity)
            {
                hash = unchecked((hash ^ character) * 16777619);
            }
            return "--access-delay:-" + (hash % 3800).ToString(CultureInfo.InvariantCulture) + "ms";
        }
    }
}
