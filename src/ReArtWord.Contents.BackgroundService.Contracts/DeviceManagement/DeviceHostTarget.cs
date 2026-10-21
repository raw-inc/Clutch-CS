namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>一括操作の開始前に固定するホストと通信実体です。</summary>
/// <param name="HostId">PCの識別子。</param>
/// <param name="ClusterId">管理窓口のクラスタ。</param>
/// <param name="PeerId">管理窓口のPeer。</param>
/// <param name="InstanceId">確認した起動実体。</param>
public sealed record DeviceHostTarget(string HostId, string ClusterId, string PeerId, Guid InstanceId);
