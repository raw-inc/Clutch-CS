using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>既存ログへの適用意思を伴う保持設定の変更です。</summary>
/// <param name="Options">新しい記録設定。</param>
/// <param name="ApplyToExisting">既に保存したログにも変更を適用するか。</param>
/// <param name="ExpandCompressed">延長した平文保持に従って既存圧縮ログを展開するか。</param>
/// <param name="Origin">変更元の端末名と任意表示名です。</param>
/// <param name="OperationId">共有保存の元操作IDです。</param>
/// <param name="ExpectedRevision">編集時に確認した共通設定の保存版です。</param>
/// <param name="DataRetention">ログと各種履歴を同じ設定から変更する場合の共通方針です。</param>
public sealed record LogOptionsChangeRequest(LogOptions Options, bool ApplyToExisting = false, bool ExpandCompressed = false, ClientAuditOrigin? Origin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Guid OperationId = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DataRetentionSettings? DataRetention = null);
