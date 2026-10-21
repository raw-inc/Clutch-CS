namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>値を通常画面へ再表示しない秘密登録です。</summary>
/// <param name="Name">参照名。</param>
/// <param name="Value">実行時にのみ解決する値。</param>
/// <param name="Origin">値を含めず変更元を確認する申告情報です。</param>
public sealed record SecretChangeRequest(string Name, string Value, ClientAuditOrigin? Origin = null);
