using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Tcp;

/// <summary>接続ごとに上限を設け、HTTP の分割受信・順序逆転を扱います。</summary>
internal sealed class TcpReassembler
{
    private static string tcpName => "TCP";
    private readonly Dictionary<(string Interface, string Source, int SourcePort, string Destination, int DestinationPort), TcpFlow> flows = new();
    private readonly long maximumRetainedBytes;

    /// <summary>取得元をまたぐ再構成バッファー全体の上限を指定します。</summary>
    internal TcpReassembler(long maximumRetainedBytes = 256 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedBytes, 1);
        this.maximumRetainedBytes = maximumRetainedBytes;
    }

    /// <summary>全方向の再構成バッファー容量。</summary>
    internal long RetainedBytes => flows.Values.Sum(flow => flow.RetainedBytes);

    /// <summary>セッション境界で接続状態を破棄します。</summary>
    internal void Clear()
    {
        flows.Clear();
    }

    /// <summary>HTTP が完成したフレームに解析結果を付加します。</summary>
    internal void Process(PacketRecord packet)
    {
        if (!string.Equals(packet.Transport, tcpName, StringComparison.Ordinal))
        {
            return;
        }

        (string, string, int, string, int) key = (packet.CaptureInterface, packet.Source, packet.SourcePort, packet.Destination, packet.DestinationPort);
        uint initial = unchecked(packet.Sequence + (uint)((packet.TcpFlags & 2) != 0 ? 1
                                                                                     : 0));
        if ((packet.TcpFlags & 2) != 0 && flows.TryGetValue(key, out TcpFlow? previous) && previous.InitialSequence != initial)
        {
            flows.Remove(key);
        }

        if (!flows.TryGetValue(key, out TcpFlow? flow))
        {
            if (128 <= flows.Count)
            {
                flows.Remove(flows.Keys.First());
            }

            flow = new TcpFlow(initial);
            flows[key] = flow;
        }

        try
        {
            flow.Add(packet);
            bool closed = flow.Finished;
            IReadOnlyList<HttpMessage> messages = flow.ReadMessages(closed);
            if (0 < messages.Count)
            {
                packet.Protocol = "HTTP";
                packet.SetHttpMessages(messages);
                packet.Paths = string.Join("\n", messages.Where(item => !item.IsResponse).Select(item => item.StartLine.Split(' ')[1].Split('?')[0]));
                packet.Summary = DisplayText.Safe(messages[^1].StartLine);
                packet.Details = string.Join("\n---\n", messages.Select(message => HttpDecoder.Describe(message)));
                packet.Details += "\nHTTP is reassembled TCP data. Hex shows the original frame.";
            }
            else
            {
                if (0 < packet.PayloadLength)
                {
                    packet.Summary += " / awaiting HTTP boundary or unidentified data";
                }
            }

            if (closed || (packet.TcpFlags & 4) != 0)
            {
                flows.Remove(key);
            }
        }
        catch (FormatException exception)
        {
            packet.Details += "\nHTTP reassembly unavailable: " + exception.Message;
            flows.Remove(key);
        }

        while (maximumRetainedBytes < RetainedBytes && 0 < flows.Count)
        {
            flows.Remove(flows.Keys.First());
        }
    }
}
