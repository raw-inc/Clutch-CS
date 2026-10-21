namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;

/// <summary>展開前の永続環境変数です。</summary>
/// <param name="Name">変数名。</param>
/// <param name="Value">展開前の値。</param>
/// <param name="Expandable">WindowsのREG_EXPAND_SZか。</param>
/// <param name="Revision">この値の内容版。</param>
public sealed record DeviceEnvironmentVariable(string Name, string Value, bool Expandable, string? Revision = null);
