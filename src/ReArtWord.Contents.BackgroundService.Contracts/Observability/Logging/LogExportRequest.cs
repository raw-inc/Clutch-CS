namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>開始時に固定するZIP出力条件です。nullの取得先は観測可能な全クラスタです。</summary>
/// <param name="Query">全ページへ適用する検索条件です。</param>
/// <param name="TimeZoneId">テキスト出力の表示タイムゾーンです。</param>
/// <param name="Source">一つのPeerだけを指定する場合の取得先です。</param>
public sealed record LogExportRequest(LogQuery Query, string TimeZoneId = "UTC", LogSearchSource? Source = null);
