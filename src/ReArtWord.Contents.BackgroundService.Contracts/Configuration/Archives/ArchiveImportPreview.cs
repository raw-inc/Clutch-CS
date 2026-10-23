namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>復元する内容と復元先の確認時点を固定した計画です。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="Confirmation">この内容と復元先の確認値です。</param>
/// <param name="Mode">選択した復元方式です。</param>
/// <param name="SourceCluster">取得元クラスターです。</param>
/// <param name="SourcePeer">ログと実有効状態を取得したピアです。</param>
/// <param name="CapturedAt">取得時刻です。</param>
/// <param name="Added">追加するWFです。</param>
/// <param name="Replaced">上書きするWFです。</param>
/// <param name="Deleted">削除するWFです。</param>
/// <param name="LogEntries">同梱ログ件数です。</param>
public sealed record ArchiveImportPreview(Guid OperationId, string Confirmation, EArchiveImportMode Mode, string SourceCluster,
    string SourcePeer, DateTimeOffset CapturedAt, IReadOnlyList<string> Added, IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Deleted, long LogEntries);
