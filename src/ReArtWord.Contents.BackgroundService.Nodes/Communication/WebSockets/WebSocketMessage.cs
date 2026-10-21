using System.Net.WebSockets;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.WebSockets;

/// <summary>先頭の断片で固定した文脈と、完全な一メッセージです。</summary>
internal sealed record WebSocketMessage(NodeExecutionContext Context, NodeValue Value, byte[] Bytes, WebSocketMessageType Type);
