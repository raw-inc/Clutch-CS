using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>同一IDの再送を新しい実行にしない分散ノード要求です。</summary>
/// <param name="Id">ノード実行と、その永続記録を特定する識別子。</param>
/// <param name="CausationId">入力となった一件の識別子。</param>
/// <param name="WorkflowId">ワークフロー識別子。</param>
/// <param name="Revision">開始時定義の版。</param>
/// <param name="Node">開始時の設定。</param>
/// <param name="Inputs">この一回の入力。</param>
/// <param name="TargetPeerId">指定Peer。</param>
/// <param name="TargetInstanceId">指定したプロセス実体。</param>
/// <param name="ExpiresAt">未開始のまま受理できる最終時刻。</param>
/// <param name="Trace">この一件の開始時に有効な詳細追跡。</param>
/// <param name="ContainsPersonalData">通常ログの値を伏せる指定。</param>
/// <param name="TimeZoneId">一件の開始時の共通タイムゾーン。</param>
/// <param name="Operational">同じ一件で共有する共通運用設定版。</param>
/// <param name="TraceSelection">この一件に固定した詳細追跡の範囲。</param>
/// <param name="Hooks">この一件につながる事象を処理済みの購読ノード。</param>
/// <param name="BufferedReceipt">完了記録の永続化を実行経路の後へ分離します。実体変更時の再実行はしません。</param>
/// <param name="IsValueRead">起動操作ではなく、他ノードへ現在値を供給するための要求時評価です。</param>
/// <param name="ReceiptId">有限期限の通信受付・結果照会に使う識別子です。省略した旧要求ではIdを使用します。</param>
public sealed record RemoteExecutionRequest(
    Guid Id,
    Guid CausationId,
    string WorkflowId,
    string Revision,
    NodeDefinition Node,
    IReadOnlyDictionary<string, NodeValue> Inputs,
    string TargetPeerId,
    Guid TargetInstanceId,
    DateTimeOffset ExpiresAt,
    bool Trace = false,
    bool ContainsPersonalData = false,
    string? TimeZoneId = null,
    ClusterOperationalSnapshot? Operational = null,
    TraceSelection? TraceSelection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkflowHookStep>? Hooks = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool BufferedReceipt = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool IsValueRead = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] Guid ReceiptId = default)
{
    /// <summary>新規要求では永続記録のUUIDv8と受付期限を持つUUIDv7を分けます。旧要求の再送では元のIdを使い続けます。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid DeliveryId => ReceiptId == Guid.Empty ? Id
                                                      : ReceiptId;

    /// <summary>通信処理が受信側の時計へ換算した受付期限です。要求の同一性を保つため、JSONや保存する要求本文には含めません。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RequestAdmission? Admission
    {
        get; init;
    }

    /// <summary>進捗表示の所有実行です。他ノードの要求・過去の保存形式へは追加しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Management.Preview.ProgressSession? Progress
    {
        get; init;
    }

    /// <summary>この経路が新規開始する待受の所有範囲です。既存の通常実行には付加しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SourceScope>? SourceScopes
    {
        get; init;
    }
}
