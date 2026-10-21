namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

/// <summary>ローカルアカウントを名前ではなくSIDで固定します。</summary>
public sealed record DeviceLoginUser(string Sid, string Name, string FullName, uint Flags, bool Selectable);
