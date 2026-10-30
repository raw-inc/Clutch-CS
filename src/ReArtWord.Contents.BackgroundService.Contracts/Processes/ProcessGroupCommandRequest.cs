namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>登録済みグループへの明示操作です。</summary>
/// <param name="Id">グループIDです。</param>
/// <param name="Command">実行する操作です。</param>
/// <param name="ExpectedVersion">確認画面で表示した登録版です。指定した版から変わっていれば操作しません。</param>
public sealed record ProcessGroupCommandRequest(string Id, EProcessGroupCommand Command, string? ExpectedVersion = null);
