namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>保持済みログを検索する条件です。</summary>
/// <param name="From">Fromの指定値。</param>
/// <param name="Until">Untilの指定値。</param>
/// <param name="Levels">Levelsの指定値。</param>
/// <param name="Peers">Peersの指定値。</param>
/// <param name="WorkflowId">WorkflowIdの指定値。</param>
/// <param name="NodeId">NodeIdの指定値。</param>
/// <param name="Text">Textの指定値。</param>
/// <param name="Limit">Limitの指定値。</param>
/// <param name="Cursor">Cursorの指定値。</param>
/// <param name="CausationId">一件の端から端までの追跡識別子。</param>
/// <param name="MaximumBytes">一ページのJSON バイト上限。</param>
/// <param name="UncompressedOnly">非圧縮の本文だけを検索します。省略時は既存APIと同じく圧縮済み本文も対象にします。</param>
public sealed record LogQuery(DateTimeOffset? From = null, DateTimeOffset? Until = null, IReadOnlyList<string>? Levels = null, IReadOnlyList<string>? Peers = null, string? WorkflowId = null, string? NodeId = null, string? Text = null, int Limit = 100, string? Cursor = null, Guid? CausationId = null, int MaximumBytes = 524288,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool UncompressedOnly = false);
