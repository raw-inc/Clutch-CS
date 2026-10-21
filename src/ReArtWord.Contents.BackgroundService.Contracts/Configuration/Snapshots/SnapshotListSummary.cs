namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元候補の識別情報です。スナップショットの状態本文は含みません。</summary>
/// <param name="Id">スナップショット識別子。過大な場合はnull。</param>
/// <param name="TargetId">対象識別子。過大な場合はnull。</param>
/// <param name="PeerId">取得元Peer。過大な場合はnull。</param>
/// <param name="Kind">設定または明示runtime状態。</param>
/// <param name="CapturedAt">取得時刻。</param>
/// <param name="Hash">内容検査値。過大な場合はnull。</param>
/// <param name="NodeIds">部分設定のノード集合。通常のnullは全体。</param>
/// <param name="IdentifierHash">ページ継続に使用する固定長識別子ハッシュ。</param>
/// <param name="DetailReason">識別情報を省略した理由。省略行は復元対象として選べません。</param>
/// <param name="ScopeOmitted">ノード集合を省略したか。true時のnullを全体復元へ読み替えません。</param>
/// <param name="Permanent">明示された永久保持かです。</param>
public sealed record SnapshotListSummary(string? Id, string? TargetId, string? PeerId, ESnapshotKind Kind, DateTimeOffset CapturedAt,
    string? Hash, IReadOnlyList<string>? NodeIds, string IdentifierHash, string? DetailReason = null, bool ScopeOmitted = false, bool Permanent = false);
