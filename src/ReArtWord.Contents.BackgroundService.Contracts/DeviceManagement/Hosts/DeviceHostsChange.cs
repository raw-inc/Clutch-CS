namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;

/// <summary>利用者が編集したhostsファイルの内容です。</summary>
/// <param name="Content">置換する全内容。</param>
public sealed record DeviceHostsChange(string Content);
