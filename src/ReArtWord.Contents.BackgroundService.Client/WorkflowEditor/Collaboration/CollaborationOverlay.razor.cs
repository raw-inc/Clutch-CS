using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>共同編集者の短命な位置表示を、ワークフロー全体から独立して更新します。</summary>
// 抑制理由: Razorが生成する公開partial宣言の固定範囲です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言と一致させます。")]
public sealed partial class CollaborationOverlay
{
    /// <summary>親が確定したカードの表示範囲です。</summary>
    // 抑制理由: Razorが親の不変スナップショットをParameterとして交換します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親が所有する表示範囲をParameterとして受け取ります。")]
    public IReadOnlyDictionary<string, (NodePosition Position, NodeSize Size)> Bounds
    {
        get; set;
    } = new Dictionary<string, (NodePosition, NodeSize)>();

    /// <summary>表示設定で有効な場合だけ処理中ノードを発光します。</summary>
    [Parameter]
    public bool HighlightRunning
    {
        get; set;
    }

    /// <summary>ブラウザー実体ごとに識別色を固定します。</summary>
    /// <returns>テーマに合う明るさの色です。</returns>
    private static string Color(Guid id) => "hsl(" + (BitConverter.ToUInt16(id.ToByteArray()) % 360).ToString(System.Globalization.CultureInfo.InvariantCulture) + " 62% 72%)";

    /// <summary>同じ表示名のブラウザーも区別できるようにします。</summary>
    /// <returns>短い実体識別を添えた編集者名です。</returns>
    private static string Label(PresenceUpdate presence) => presence.DisplayName + " · " + presence.UiInstanceId.ToString("N")[..4];

    /// <summary>他の編集者の位置更新をこの表示だけで購読します。</summary>
    protected override void OnInitialized()
    {
        Collaboration.PresenceChanged += RequestRenderFromEvent;
        Activity.Changed += RequestRenderFromEvent;
    }

    /// <summary>画面切替後は旧オーバーレイへ更新を配信しません。</summary>
    /// <returns>通知購読と画面資源の解放です。</returns>
    // 抑制理由: Blazorの固定終了署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定終了署名です。")]
    public override async ValueTask DisposeAsync()
    {
        Collaboration.PresenceChanged -= RequestRenderFromEvent;
        Activity.Changed -= RequestRenderFromEvent;
        await base.DisposeAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }
}
