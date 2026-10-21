using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>現在の待機順だけを、図内とダッシュボードに適した密度で表示します。</summary>
// 抑制理由: Razorが生成する公開partial宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言と一致させます。")]
public sealed partial class ConcurrencyQueuePanel
{
    /// <summary>入力値や保存履歴を含まない確認済みの現在値です。</summary>
    [Parameter]
    public WorkflowConcurrencySnapshot? Snapshot
    {
        get; set;
    }

    /// <summary>図内では実行番号と期限列を省き、短いカード内へ収めます。</summary>
    [Parameter]
    public bool Compact
    {
        get; set;
    }

    /// <summary>端末間の時計差を使わず、同じ担当が採取した時刻差を表示します。</summary>
    /// <returns>秒単位の非負の経過です。</returns>
    private static string Duration(TimeSpan value) => Math.Max(0, value.TotalSeconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
}
