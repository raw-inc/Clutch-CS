namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>事前確認で取得した復元内容と確認トークン。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Confirmation">復元内容と復元先の設定を照合する確認トークン。</param>
/// <param name="Mode">選択した復元方式です。</param>
/// <param name="SourceCluster">取得元クラスターです。</param>
/// <param name="SourcePeer">書き出しを受け付けたピア。</param>
/// <param name="CapturedAt">取得時刻です。</param>
/// <param name="Added">追加するWFのID。</param>
/// <param name="Replaced">上書きするWFのID。</param>
/// <param name="Deleted">削除するWFのID。</param>
/// <param name="LogEntries">アーカイブに含まれるログ件数。</param>
public sealed record ArchiveImportPreview(Guid OperationId, string Confirmation, EArchiveImportMode Mode, string SourceCluster,
    string SourcePeer, DateTimeOffset CapturedAt, IReadOnlyList<string> Added, IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Deleted, long LogEntries);
