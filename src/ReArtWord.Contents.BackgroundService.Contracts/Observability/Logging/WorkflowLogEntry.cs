using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>Peerを跨いで検索するログの一件です。</summary>
/// <param name="Id">重複転送を判別する識別子。</param>
/// <param name="Time">記録時刻。</param>
/// <param name="Level">DEBUG、INFO、WARN、ERROR、FATAL。</param>
/// <param name="PeerId">記録元Peer。</param>
/// <param name="Message">人向けの説明。</param>
/// <param name="WorkflowId">関連ワークフロー。</param>
/// <param name="NodeId">関連ノード。</param>
/// <param name="CausationId">一件の因果識別子。</param>
/// <param name="Data">記録する値。</param>
/// <param name="ContainsPersonalData">通常ログで値を伏せるか。</param>
/// <param name="Raw">通信ペイロードの範囲と方向。認証ヘッダーは含みません。</param>
/// <param name="Hooks">このログを生んだ処理が既に通った購読ノード。</param>
public sealed record WorkflowLogEntry(
    Guid Id,
    DateTimeOffset Time,
    string Level,
    string PeerId,
    string Message,
    string? WorkflowId = null,
    string? NodeId = null,
    Guid? CausationId = null,
    NodeValue? Data = null,
    bool ContainsPersonalData = false,
    TraceRawInfo? Raw = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkflowHookStep>? Hooks = null);
