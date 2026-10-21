namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>選択したPeerが現在追跡している実行をページ単位で照会します。</summary>
/// <param name="Limit">一ページの最大件数。</param>
/// <param name="After">前ページの続き位置。先頭から読む場合はnull。</param>
public sealed record ActiveRunsQuery(int Limit = 50, Guid? After = null);
