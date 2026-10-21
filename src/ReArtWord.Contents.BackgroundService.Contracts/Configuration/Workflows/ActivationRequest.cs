using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>保存版を新しい処理へ明示反映します。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="WorkflowId">対象定義。</param>
/// <param name="Revision">保存済み版。</param>
/// <param name="ImpactConfirmed">確認付き方針の影響確認。</param>
/// <param name="ImpactToken">確認画面で取得した候補と有効版の識別子。</param>
public sealed record ActivationRequest(Guid OperationId, string WorkflowId, string Revision, bool ImpactConfirmed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImpactToken = null);
