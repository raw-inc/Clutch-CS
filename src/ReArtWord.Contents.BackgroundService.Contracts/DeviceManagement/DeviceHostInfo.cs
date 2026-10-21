namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>同一PCのPeerを一件にまとめた、実照会済みの能力です。</summary>
/// <param name="Target">読取り時に確認した実体。</param>
/// <param name="Capabilities">確認できた対応領域。</param>
/// <param name="Problem">未対応または照会不能の理由。</param>
public sealed record DeviceHostInfo(DeviceHostTarget Target, DeviceCapabilities? Capabilities, string? Problem = null);
