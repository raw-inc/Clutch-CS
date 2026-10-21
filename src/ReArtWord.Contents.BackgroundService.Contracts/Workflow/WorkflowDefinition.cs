using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>保存・共有する汎用ワークフロー定義です。</summary>
/// <param name="Id">ワークフロー識別子。</param>
/// <param name="Name">表示名。</param>
/// <param name="Nodes">構成ノード。</param>
/// <param name="Wires">接続。</param>
/// <param name="Enabled">自動入力を受け付けるか。</param>
/// <param name="Trace">常設の詳細追跡設定。</param>
/// <param name="ContainsPersonalData">通常ログで値を伏せるか。</param>
/// <param name="TraceSelection">保存する通常追跡の範囲。省略時は秘密を除く全範囲。</param>
/// <param name="ActivationRules">変更種類別の反映方針。未指定の種類は自動です。</param>
public sealed record WorkflowDefinition(
    string Id,
    string Name,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<WireDefinition> Wires,
    bool Enabled = false,
    bool Trace = false,
    bool ContainsPersonalData = false,
    TraceSelection? TraceSelection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<EWorkflowChangeKind, EActivationPolicy>? ActivationRules = null)
{
    /// <summary>公開入出力がゼロでも再利用定義であることを明示できます。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRecipe
    {
        get; init;
    }

    /// <summary>実行グラフではなく、他WFのノードを参照するWFDashboardです。作成後の種類変更は行いません。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDashboard
    {
        get; init;
    }
}
