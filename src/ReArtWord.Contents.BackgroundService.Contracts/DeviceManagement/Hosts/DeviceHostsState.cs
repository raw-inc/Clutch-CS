namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;

/// <summary>読み込んだhostsファイルです。</summary>
/// <param name="Content">コメントと改行を含む内容。</param>
public sealed record DeviceHostsState(string Content);
