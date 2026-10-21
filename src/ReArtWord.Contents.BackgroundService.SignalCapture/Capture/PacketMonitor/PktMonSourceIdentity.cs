namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>表示名に依存せず、同じ NIC の登録 ID と親スイッチを区別します。</summary>
internal sealed record PktMonSourceIdentity(uint Id, uint SecondaryId, uint ParentId, Guid InterfaceId, bool IsSwitchPort = false);
