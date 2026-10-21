using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

/// <summary>保存とスナップショットを確認した共通設定版を明示適用します。</summary>
/// <param name="OperationId">一回の適用識別子です。</param>
/// <param name="Revision">明示した保存版です。</param>
/// <param name="ImpactConfirmed">確認必須方針の影響を確認したかです。</param>
/// <param name="ImpactToken">比較した候補と有効版の確認識別子です。</param>
public sealed record ClusterOperationalActivateRequest(Guid OperationId, string Revision, bool ImpactConfirmed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImpactToken = null);
