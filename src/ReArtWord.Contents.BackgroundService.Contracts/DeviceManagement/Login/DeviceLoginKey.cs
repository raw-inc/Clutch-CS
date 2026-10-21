namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

/// <summary>このCoreの稼働中だけ有効なパスワード暗号化用公開鍵です。</summary>
public sealed record DeviceLoginKey(string Id, string Spki);
