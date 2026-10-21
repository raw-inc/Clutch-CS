namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>変更操作を後から調べるための申告情報です。本人証明ではありません。</summary>
/// <param name="MachineName">操作を受け付けたClientHostの端末名。</param>
/// <param name="DisplayName">任意の表示名。</param>
public sealed record ClientAuditOrigin(string MachineName, string? DisplayName = null);
