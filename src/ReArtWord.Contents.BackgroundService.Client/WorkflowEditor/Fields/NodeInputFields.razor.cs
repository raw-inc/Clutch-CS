using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>ノードの固定入力を型と接続条件に従って編集します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class NodeInputFields
{
    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    private readonly Dictionary<string, JsonValueKind> focusedKinds = new(StringComparer.Ordinal);

    /// <summary>固定入力の編集開始時点を保持します。</summary>
    private void BeginInput(string port, JsonValueKind kind) => focusedKinds.TryAdd(port, kind);

    /// <summary>固定入力の編集中状態を解除します。</summary>
    private void EndInput(string port) => focusedKinds.Remove(port);

    /// <summary>固定入力を編集するノード定義です。</summary>
    [Parameter, EditorRequired]
    public NodeDefinition Node
    {
        get;
        set;
    } = null!;

    /// <summary>固定入力の型と検証に使うポート定義です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<PortDescriptor> Ports
    {
        get;
        set;
    } = [];

    /// <summary>ノードカード内のコンパクトな入力欄として表示するかどうかです。</summary>
    [Parameter]
    public bool Inline
    {
        get;
        set;
    }

    /// <summary>入力内容の検証不能状態を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<bool> InvalidChanged
    {
        get;
        set;
    }

    /// <summary>入力値を検証して編集対象へ反映し、検証状態を通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task CommitAsync(string port, string raw, bool binary, JsonValueKind kind)
    {
        NodeInputEditing.Commit(Model, Node.Id, port, raw, binary, kind);
        await InvalidChanged.InvokeAsync(Model.HasInvalidInputs).ConfigureAwait(true);
    }
}
