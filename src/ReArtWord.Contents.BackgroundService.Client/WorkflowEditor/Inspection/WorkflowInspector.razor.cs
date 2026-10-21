using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;

/// <summary>ワークフロー全体の接続と追跡設定を編集します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class WorkflowInspector
{
    /// <summary>詳細表示を開いたときだけ接続とワークフロー設定を描画します。</summary>
    [Parameter]
    public bool Visible
    {
        get; set;
    } = true;

    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    /// <summary>ワークフローの詳細追跡の有効状態を変更します。</summary>
    private void Trace(Func<TraceSelection, TraceSelection> edit)
    {
        Model.Change(document => document with { TraceSelection = edit(document.TraceSelection ?? new()) });
    }

    /// <summary>未指定の変更種類は従来どおり自動反映を示します。</summary>
    /// <returns>草稿に保存する反映条件です。</returns>
    private EActivationPolicy ActivationRule(EWorkflowChangeKind kind) => Model.Draft.Document.ActivationRules?.GetValueOrDefault(kind) ?? EActivationPolicy.Auto;

    /// <summary>復元保護とUndoに従って一つの変更種類の方針を更新します。</summary>
    private void ChangeActivationRule(EWorkflowChangeKind kind, ChangeEventArgs args)
    {
        if (!Enum.TryParse(args.Value?.ToString(), out EActivationPolicy policy) || !Enum.IsDefined(policy))
        {
            return;
        }
        Model.Change(document =>
        {
            Dictionary<EWorkflowChangeKind, EActivationPolicy> rules = new(document.ActivationRules ?? new Dictionary<EWorkflowChangeKind, EActivationPolicy>());
            rules[kind] = policy;
            return document with { ActivationRules = rules };
        });
    }

    /// <summary>追跡に使用する制限値をワークフローへ反映します。</summary>
    private void TraceLimit(ChangeEventArgs args)
    {
        string raw = args.Value?.ToString() ?? string.Empty;
        int? limit = null;
        if (raw.Length != 0)
        {
            if (!int.TryParse(raw, out int value) || value < 0)
            {
                Model.SetInput("workflow/traceRawLimit", raw);
                return;
            }

            limit = value;
        }

        Model.SetInput("workflow/traceRawLimit", null);
        Trace(value => value with { RawByteLimit = limit });
    }

    /// <summary>編集に使用するノード定義の一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<NodeDescriptor> Catalog
    {
        get;
        set;
    } = [];

    /// <summary>ワークフローの接続に使用できるルート名です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<string> Routes
    {
        get;
        set;
    } = ["success", "failure", "notExecuted", "unknown", "cancelled", "*", "starting", "listening", "completed"];

    /// <summary>指定した接続の編集値を草稿へ反映します。</summary>
    private void Wire(int index, Func<WireDefinition, WireDefinition> edit) => Model.Change(document => document with { Wires = document.Wires.Select((wire, i) => i == index ? edit(wire)
                                                                                                                                                                              : wire).ToArray() });

    /// <summary>編集保護を確認して接続を草稿から削除します。</summary>
    private void RemoveWire(int index) => GraphDeletion.Wire(Model, index);
}
