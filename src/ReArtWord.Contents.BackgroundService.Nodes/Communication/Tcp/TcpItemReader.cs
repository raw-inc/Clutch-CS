using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Tcp;

/// <summary>TCP接続の一件を読み、同じ接続への固定返信またはワークフロー返信を完了します。</summary>
internal sealed class TcpItemReader(string format, CommunicationReplyRegistry replies)
{
    private static string MatchOsc => "osc";

    /// <summary>接続のフレームと明示返信だけを処理します。</summary>
    /// <returns>一件の結果、正常EOFはnullです。</returns>
    internal async Task<TcpItemResult> ReadAsync(NodeSourceContext context, TcpClient client, NodeExecutionContext item, Func<NodeExecutionContext, bool> admit, CancellationToken cancellationToken = default)
    {
        bool replying = false;
        bool admitted = false;
        NodeResult? emitted = null;
        try
        {
            NodeSettings settings = new(item.Settings);
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
            NetworkStream stream = client.GetStream();
            string framing = settings.ReadText("framing", "lengthPrefix") ?? string.Empty;
            byte[]? bytes = await TcpFrame.ReadAsync(stream, framing, settings.ReadInt32("maxBytes", 65507), deadline.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return new(null);
            }
            await CommunicationTrace.WriteAsync(item, "tcp:" + format, false, bytes, CancellationToken.None).ConfigureAwait(false);
            NodeResult? decoded = string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Decode(bytes, item.Settings)
                                                                                            : NodeResult.Success(CommunicationSupport.Decode(bytes, format));
            if (decoded is null)
            {
                return new(NodeResult.Success(), true);
            }
            if (!admit(item))
            {
                return new(null);
            }
            admitted = true;
            if (decoded.Outcome != EOutcome.Succeeded)
            {
                return new(decoded);
            }
            string replyMode = settings.ReadText("replyMode", "fixed") ?? string.Empty;
            string workflow = "workflow";
            if (string.Equals(replyMode, workflow, StringComparison.Ordinal))
            {
                TcpReplyWriter writer = new(client, format, framing, settings.ReadInt32("maxBytes", 65507), item);
                using ReplyRequestLease pending = replies.Register(item.PeerId, "tcp", writer.WriteAsync, deadline.Token);
                NodeResult result = decoded;
                Dictionary<string, NodeValue> outputs = new(result.Outputs, StringComparer.Ordinal)
                {
                    ["request"] = pending.Handle,
                };
                emitted = result with { Outputs = outputs };
                await context.EmitAsync(item, emitted, cancellationToken).ConfigureAwait(false);
                NodeResult response = await pending.Completion.ConfigureAwait(false);
                return new(emitted, true, response.Outcome != EOutcome.Succeeded);
            }
            JsonElement reply = settings.Get("reply");
            if (reply.ValueKind != JsonValueKind.Undefined)
            {
                byte[] response = CommunicationSupport.Encode(NodeValue.FromJson(reply), format);
                await CommunicationTrace.WriteAsync(item, "tcp:" + format, true, response, CancellationToken.None).ConfigureAwait(false);
                replying = true;
                await TcpFrame.WriteAsync(stream, response, framing, deadline.Token).ConfigureAwait(false);
            }
            return new(decoded);
        }
        catch (Exception exception) when (exception is IOException or SocketException or FormatException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            if (emitted is not null)
            {
                return new(emitted, true, true);
            }
            if (!admitted && !admit(item))
            {
                return new(null);
            }
            EOutcome outcome = EOutcome.Failed;
            if (replying)
            {
                outcome = EOutcome.Unknown;
            }
            return new(NodeResult.Failure("network.tcp.item.failed", "TCP入力または返信の完了を確認できません。", outcome));
        }
    }
}
