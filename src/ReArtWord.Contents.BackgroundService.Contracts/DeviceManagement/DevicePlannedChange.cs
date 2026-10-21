namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>一括操作の一対象と、その設定変更です。</summary>
/// <param name="Target">固定した通信先。</param>
/// <param name="Request">固定した変更内容。</param>
public sealed record DevicePlannedChange(DeviceHostTarget Target, DeviceChangeRequest Request);
