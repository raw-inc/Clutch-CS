using ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Tcp;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Udp;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication;

/// <summary>汎用通信能力を共通ノードカタログへ登録します。</summary>
public static class CommunicationNodeRegistration
{
    /// <summary>依存を注入した通信ノード一覧を作成します。</summary>
    /// <returns>一つずつ登録できる通信ノードです。</returns>
    public static IReadOnlyList<INode> Create(ISecretStore secrets, CommunicationReplyRegistry? replies = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        CommunicationReplyRegistry registry = replies ?? new();
        return [
            new Osc.OscPacketNode(),
            new Osc.OscParseNode(),
            new DatagramSendNode("udp.send", "raw"),
            new DatagramReceiveNode("udp.receive", "raw"),
            new DatagramSendNode("osc.udp.send", "osc"),
            new DatagramReceiveNode("osc.udp.receive", "osc"),
            new DatagramSendNode("artnet.send", "artnet"),
            new DatagramReceiveNode("artnet.receive", "artnet"),
            new TcpRequestNode("tcp.request", "raw"),
            new TcpListenNode("tcp.listen", "raw", registry),
            new TcpRequestNode("osc.tcp.request", "osc"),
            new TcpListenNode("osc.tcp.listen", "osc", registry),
            new HttpRequestNode(secrets),
            new HttpResponseParseNode(),
            new HttpListenNode(registry),
            new WebSocketRequestNode(secrets),
            new WebSocketListenNode(registry),
            new ReplyNode("websocket", registry),
            new ReplyNode("http", registry),
            new ReplyNode("tcp", registry)
        ];
    }
}
