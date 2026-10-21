namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;

/// <summary>指定スコープの永続環境変数です。</summary>
/// <param name="Scope">MachineまたはUser。</param>
/// <param name="UserSid">明示された利用者。</param>
/// <param name="Variables">変数の一覧。</param>
public sealed record DeviceEnvironmentState(string Scope, string? UserSid, IReadOnlyList<DeviceEnvironmentVariable> Variables);
