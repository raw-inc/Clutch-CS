namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>所有Peerが許可検査・正規化した実Host上のアクセス範囲です。</summary>
public sealed record FileFootprint(string PeerId, Guid InstanceId, string HostId, string Path, bool Write);
