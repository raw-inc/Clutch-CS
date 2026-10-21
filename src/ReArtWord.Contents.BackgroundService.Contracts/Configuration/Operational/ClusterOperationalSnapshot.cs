using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

/// <summary>一件の入力や接続の開始時に所有して固定する運用設定版です。</summary>
/// <param name="Settings">共通運用値です。</param>
/// <param name="Revision">内容に対応する固定版です。</param>
/// <param name="SavedAt">永続保存の時点です。</param>
/// <param name="DerivedFromVersions">競合の非依存部分を導出した元候補版です。</param>
/// <param name="UnresolvedFields">新規利用を許さない未解消項目です。</param>
public sealed record ClusterOperationalSnapshot(ClusterOperationalSettings Settings, string Revision, DateTimeOffset SavedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DerivedFromVersions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnresolvedFields = null);
