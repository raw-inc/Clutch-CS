using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>一つのノードまたは安全Groupの明示停止を永続操作で変更します。</summary>
/// <param name="OperationId">再照会する操作識別子。</param>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="Scope">ノード IDまたは安全Group。</param>
/// <param name="Stopped">新規入力を停止するか。</param>
/// <param name="ExpectedVersion">画面で確認した版。稼働操作は受付側の最新状態へ順番に適用します。</param>
/// <param name="Continued">自動停止を人が一時的に続行させる指定。Stoppedと同時指定は不可。</param>
/// <param name="ImpactConfirmed">要求された対象・影響確認を今回行ったか。</param>
/// <param name="ExpectedWorkflowRevision">確認画面が示した対象定義の版です。</param>
public sealed record ManualSafetyChange(Guid OperationId, string WorkflowId, string Scope, bool Stopped, string? ExpectedVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Continued = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ImpactConfirmed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedWorkflowRevision = null);
