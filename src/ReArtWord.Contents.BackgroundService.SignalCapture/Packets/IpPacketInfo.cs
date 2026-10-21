namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>原本内の検証済み IP とトランスポートの位置。</summary>
internal readonly record struct IpPacketInfo(int Offset, int Length, int Version, int SourceOffset, int DestinationOffset, int AddressLength, int TransportOffset, byte Transport, bool NonInitialFragment);
