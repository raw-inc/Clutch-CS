using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>セッション世代と取得元を伴う未解析の原本。</summary>
internal sealed record QueuedPacket(long Generation, string SourceId, PacketRecord Packet);
