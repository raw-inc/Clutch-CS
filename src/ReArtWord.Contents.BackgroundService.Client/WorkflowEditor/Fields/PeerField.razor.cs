using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>ピア候補と手入力を一つの欄で提供します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class PeerField
{
    private readonly string listId = "peers-" + Guid.NewGuid().ToString("N");

    /// <summary>Host単位で重複を除いたPC選択欄にします。</summary>
    [Parameter]
    public bool Hosts
    {
        get; set;
    }

    /// <summary>選択または直接入力したピアID。</summary>
    [Parameter]
    public string Value
    {
        get; set;
    } = string.Empty;

    /// <summary>配線中・保護中の編集を抑止します。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>ピア選択の変更です。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get; set;
    }
}
