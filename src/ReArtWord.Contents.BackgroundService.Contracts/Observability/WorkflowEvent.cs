using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Observability;

/// <summary>通常ノードから後続処理へ渡す観測済み事象です。</summary>
/// <param name="Id">事象識別子。</param>
/// <param name="Name">安定した事象名。</param>
/// <param name="Time">観測時刻。</param>
/// <param name="PeerId">観測Peer。</param>
/// <param name="Data">事象の値。</param>
/// <param name="WorkflowId">発生元ワークフロー。</param>
/// <param name="NodeId">発生元ノード。</param>
/// <param name="CausationId">原因事象。</param>
/// <param name="PeerInstanceId">事象を発行したPeerの起動ID。</param>
/// <param name="Hooks">今回の事象につながる処理で既に通った購読ノード。</param>
/// <param name="DetailReason">値を省略した場合などの理由。未設定なら省略なし。</param>
public sealed record WorkflowEvent(
    Guid Id,
    string Name,
    DateTimeOffset Time,
    string PeerId,
    NodeValue Data,
    string? WorkflowId = null,
    string? NodeId = null,
    Guid? CausationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? PeerInstanceId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkflowHookStep>? Hooks = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DetailReason = null);
