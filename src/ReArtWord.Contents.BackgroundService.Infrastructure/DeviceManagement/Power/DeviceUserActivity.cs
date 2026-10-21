namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>時刻計算の揺らぎとは独立した入力識別子を持つ確認結果です。</summary>
public sealed record DeviceUserActivity(int SessionId, DateTimeOffset LastActivity, string Identity);
