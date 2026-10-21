using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>一つのノードの設定・実行先・定数入力を保持します。</summary>
/// <param name="Id">ワークフロー内の識別子。</param>
/// <param name="Type">ノード種類。</param>
/// <param name="Settings">ノード固有設定。</param>
/// <param name="Inputs">定数入力。配線と同じポートには指定できません。</param>
/// <param name="Target">実行先。</param>
/// <param name="Safety">明示した安全停止方針。</param>
/// <param name="Position">画面上の位置。</param>
/// <param name="StartOnLoad">有効化時にSourceの待受を開始するか。有限処理には指定しません。</param>
/// <param name="Overlap">同じ実行Peerでの前回実行との重複方針。</param>
/// <param name="PcExecution">PC選択入力を使用するときの実行台数。</param>
/// <param name="ContractVersion">この設定が期待する動作契約の版。未指定は初回契約1。</param>
/// <param name="ListenDurationMs">Sourceの開始から受付を正常終了するまでの任意時間。未指定は常時待受。受付済み処理の期限とは別です。</param>
/// <param name="Size">コードノードのエディター表示寸法。実行には使用しません。</param>
/// <param name="LogicalName">表示と検索に使う任意の論理名。重複を許可し、実行時の識別には使用しません。</param>
public sealed record NodeDefinition(
    string Id,
    string Type,
    JsonElement Settings,
    IReadOnlyDictionary<string, NodeValue>? Inputs = null,
    TargetDefinition? Target = null,
    NodeSafetyPolicy? Safety = null,
    NodePosition? Position = null,
    bool StartOnLoad = false,
    EOverlapPolicy Overlap = EOverlapPolicy.Parallel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] EPcExecutionMode PcExecution = EPcExecutionMode.One,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContractVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ListenDurationMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NodeSize? Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogicalName = null);
