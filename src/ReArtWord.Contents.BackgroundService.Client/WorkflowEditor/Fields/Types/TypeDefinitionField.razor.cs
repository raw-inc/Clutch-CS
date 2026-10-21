using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields.Types;

/// <summary>型の再帰編集をノードの小さい欄から開きます。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class TypeDefinitionField
{
    /// <summary>公開境界の最上位でバイナリと実行PC選択を許可します。</summary>
    [Parameter]
    public bool IncludeRuntimeTypes
    {
        get; set;
    }

    private bool open;

    /// <summary>現在の型です。</summary>
    [Parameter]
    public JsonElement Value
    {
        get; set;
    }

    /// <summary>読み取り専用にします。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>変更後の型です。</summary>
    [Parameter]
    public EventCallback<JsonElement> ValueChanged
    {
        get; set;
    }

    /// <summary>無効な編集内容を通知します。</summary>
    [Parameter]
    public EventCallback<string?> InvalidChanged
    {
        get; set;
    }
}
