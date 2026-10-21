using System.Text.Json;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Tcp;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

/// <summary>原本の解析と、上限付きメタデータの符号化を一つの出力スレッドで行います。</summary>
internal sealed class PacketEncoder
{
    private readonly TcpReassembler tcp = new(32 * 1024 * 1024);

    /// <summary>停止・再開境界で全再構成バッファーを解放します。</summary>
    internal void Clear() => tcp.Clear();

    /// <summary>取得元・観測点を隔離して解析し、kind 2 を生成します。</summary>
    /// <returns>長さヘッダーを含む原本フレーム。</returns>
    internal byte[] Encode(QueuedPacket item)
    {
        PacketRecord packet = item.Packet;
        string observation = packet.ObservationPoint.Length == 0 ? item.SourceId
                                                                 : packet.ObservationPoint;
        packet.CaptureInterface = item.SourceId + "/" + observation;
        FrameDecoder.Decode(packet);
        tcp.Process(packet);
        bool ports = packet.Transport is "TCP" or "UDP";
        int? sourcePort = null;
        int? destinationPort = null;
        if (ports)
        {
            sourcePort = packet.SourcePort;
            destinationPort = packet.DestinationPort;
        }

        var metadata = new PacketMetadata(item.SourceId, observation, packet.Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), packet.LinkType, packet.OriginalLength,
            packet.Source, packet.Destination, sourcePort, destinationPort,
            packet.Protocol, packet.Transport, Bound(packet.Summary, 512), Bound(ProtocolDecoder.Describe(packet), 16384), Bound(packet.Paths, 2048), Bound(packet.Value, 256), PacketFingerprint.Compute(packet.Data, packet.LinkType));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, PipeJsonContext.Default.PacketMetadata);
        return PipeFraming.Packet(json, packet.Data);
    }

    /// <summary>サロゲートペアの途中で切断しない文字列上限。</summary>
    /// <returns>上限内の文字列。</returns>
    private static string Bound(string text, int maximum)
    {
        if (text.Length <= maximum)
        {
            return text;
        }
        if (char.IsHighSurrogate(text[maximum - 1]))
        {
            maximum--;
        }
        return text[..maximum];
    }
}
