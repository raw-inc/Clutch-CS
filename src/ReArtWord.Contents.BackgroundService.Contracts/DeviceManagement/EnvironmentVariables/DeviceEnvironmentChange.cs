namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;

/// <summary>一つの環境変数の明示変更です。</summary>
/// <param name="Value">保存する値。削除時はnull。</param>
/// <param name="Delete">変数自体を削除するか。</param>
/// <param name="Expandable">値内の環境変数を展開するか。</param>
public sealed record DeviceEnvironmentChange(string? Value, bool Delete = false, bool Expandable = true);
