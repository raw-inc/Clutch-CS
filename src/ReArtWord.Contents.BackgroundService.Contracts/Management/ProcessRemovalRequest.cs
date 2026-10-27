namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>画面で確認した版のアプリケーション登録だけを削除します。</summary>
public sealed record ProcessRemovalRequest(string Id, string ExpectedVersion);
