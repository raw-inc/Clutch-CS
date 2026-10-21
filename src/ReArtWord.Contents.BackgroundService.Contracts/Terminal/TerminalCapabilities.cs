namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>選択したCoreで実際に取得可能なターミナル権限です。</summary>
/// <param name="UserAvailable">通常ユーザートークンを取得できるかです。</param>
/// <param name="AdministratorAvailable">SYSTEMではないユーザーの昇格トークンを取得できるかです。</param>
/// <param name="UserReason">通常起動ができない理由です。</param>
/// <param name="AdministratorReason">管理者起動ができない理由です。</param>
/// <param name="AdministratorUsesSystem">未ログオンのため管理者モードがSYSTEMになるかです。</param>
/// <param name="Settings">Windows Coreの恒久設定です。nullは変更機能を提供しないCoreです。</param>
public sealed record TerminalCapabilities(bool UserAvailable, bool AdministratorAvailable, string? UserReason = null, string? AdministratorReason = null, bool AdministratorUsesSystem = false, TerminalSettings? Settings = null);
