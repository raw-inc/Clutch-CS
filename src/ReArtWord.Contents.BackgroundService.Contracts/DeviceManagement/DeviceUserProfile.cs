namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>環境変数の編集先となる読込み済み利用者です。</summary>
/// <param name="Sid">利用者SID。</param>
/// <param name="Name">表示用アカウント名。</param>
public sealed record DeviceUserProfile(string Sid, string Name);
