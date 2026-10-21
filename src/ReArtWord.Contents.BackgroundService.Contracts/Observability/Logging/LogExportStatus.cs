namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>画面が追跡する一時出力の状態です。検索本文や秘密のパスは含みません。</summary>
/// <param name="Id">推測困難な出力識別子です。</param>
/// <param name="State">queued、running、completed、failed、cancelledです。</param>
/// <param name="Entries">出力済みのログ件数です。</param>
/// <param name="Complete">検索範囲と記録詳細に未取得・省略がないかです。</param>
/// <param name="Problem">失敗理由のコードです。</param>
/// <param name="FileName">保存用の安全なファイル名です。</param>
/// <param name="ExpiresAt">未開始の受付または送信状態の保持期限です。ファイルは保持しません。</param>
public sealed record LogExportStatus(Guid Id, string State, long Entries, bool Complete, string? Problem, string FileName, DateTimeOffset ExpiresAt);
