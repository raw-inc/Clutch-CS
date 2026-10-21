using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Tcp;

/// <summary>受付時のformat/framingでワークフロー応答を書き込みます。</summary>
internal sealed class TcpReplyWriter
{
    private readonly TcpClient client;
    private readonly string format;
    private readonly string framing;
    private readonly int maximum;
    private readonly NodeExecutionContext traceContext;

    /// <summary>返信対象の一接続と設定版を固定します。</summary>
    internal TcpReplyWriter(TcpClient client, string format, string framing, int maximum, NodeExecutionContext traceContext)
    {
        this.client = client;
        this.format = format;
        this.framing = framing;
        this.maximum = maximum;
        this.traceContext = traceContext;
    }

    /// <summary>形式不正・未接続と、送信後不明を区別します。</summary>
    /// <returns>ローカルTCP書込みの確認結果です。</returns>
    internal async Task<NodeResult> WriteAsync(NodeValue value, CancellationToken cancellationToken = default)
    {
        bool attempted = false;
        try
        {
            byte[] bytes = CommunicationSupport.Encode(value, format);
            TcpFrame.ValidateWrite(bytes, framing);
            if (maximum < bytes.Length || !client.Connected || (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0))
            {
                return NodeResult.Failure("tcp.reply.unavailable", "返信上限または接続状態を確認できません。", EOutcome.NotExecuted);
            }
            cancellationToken.ThrowIfCancellationRequested();
            attempted = true;
            await CommunicationTrace.WriteAsync(traceContext, "tcp:" + format, true, bytes, CancellationToken.None).ConfigureAwait(false);
            await TcpFrame.WriteAsync(client.GetStream(), bytes, framing, cancellationToken).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(new { bytesSent = bytes.Length, confirmation = "localWrite" }));
        }
        catch (Exception exception) when (exception is IOException or SocketException or ArgumentException or InvalidOperationException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or OverflowException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (attempted)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("tcp.reply.unconfirmed", "TCP返信の完了を確認できません。", outcome);
        }
    }
}
