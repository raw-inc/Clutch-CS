namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>画面の全体選択と独立して固定実体を読む要求です。</summary>
/// <param name="Target">選択済みHostとPeer実体。</param>
/// <param name="Query">設定領域と対象。</param>
public sealed record DeviceTargetRead(DeviceHostTarget Target, DeviceReadRequest Query);
