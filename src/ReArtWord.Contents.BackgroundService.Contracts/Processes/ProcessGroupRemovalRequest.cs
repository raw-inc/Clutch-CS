namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>表示時の登録版を指定してグループを削除します。</summary>
/// <param name="Id">削除するグループIDです。</param>
/// <param name="ExpectedVersion">確認済みの登録版です。</param>
public sealed record ProcessGroupRemovalRequest(string Id, string ExpectedVersion);
