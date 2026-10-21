namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>転送開始時に固定し、再起動後の別実体へ付け替えない対象です。</summary>
/// <param name="ClusterId">クラスタIDです。</param>
/// <param name="PeerId">ピアIDです。</param>
/// <param name="InstanceId">起動実体IDです。</param>
/// <param name="Port">ClientHostが実発見で確認した通信ポートです。</param>
public sealed record FileTransferPeer(string ClusterId, string PeerId, Guid InstanceId, int Port = 0);
