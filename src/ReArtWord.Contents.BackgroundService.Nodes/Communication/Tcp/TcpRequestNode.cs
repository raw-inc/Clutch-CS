using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Tcp;

/// <summary>明示NICからTCP接続し、一件送信と任意の返信受信を行います。</summary>
internal sealed class TcpRequestNode : IActionNode
{
    private static string MatchOsc => "osc";

    private readonly string format;

    /// <summary>未加工のまたはOSC形式のTCP処理を作成します。</summary>
    internal TcpRequestNode(string type, string format)
    {
        this.format = format;
        Descriptor = new(type, type, "Communication", "明示TCPフレームで送信し、設定時に一件返信を待ちます。", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
            [new("value", CommunicationSupport.EncodedKind(format), true)], [new("value")], ["success"], CommunicationSupport.Schema(true, "tcp")) { HasAsyncCompletion = true };
        if (string.Equals(format, MatchOsc, StringComparison.Ordinal))
        {
            Descriptor = Descriptor with { Inputs = [.. Descriptor.Inputs, new("address", EPortValueKind.Json, OverridesSetting: "address")], Outputs = [.. Descriptor.Outputs, new("address", EPortValueKind.Json), new("arguments", EPortValueKind.Json)], SettingsSchema = OscNodeValues.Schema(Descriptor.SettingsSchema, true) };
        }
    }

    /// <summary>通信設定です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>通信先とフレーム設定を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. NodeSchemaValidator.Validate(definition, Descriptor), .. CommunicationSupport.Validate(definition, true), .. (string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Validate(definition)
                                                                                                                                                                                                                   : [])];
        string osc = "osc";
        string prefix = "lengthPrefix";
        string? framing = new NodeSettings(definition.Settings).ReadText("framing", prefix);
        if (string.Equals(format, osc, StringComparison.Ordinal) && !string.Equals(framing, prefix, StringComparison.Ordinal))
        {
            issues.Add(new("osc.tcp.framing.invalid", "OSC over TCPはlengthPrefix framingが必要です。", definition.Id));
        }
        return issues;
    }

    /// <summary>一接続を所有し、暗黙再接続なしで結果を返します。</summary>
    /// <returns>成功または副作用不明を含む結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        bool sending = false;
        try
        {
            NodeSettings settings = new(context.Settings);
            LocalNetworkBinding binding = new(context.Settings, context.HostId);
            string framing = settings.ReadText("framing", "lengthPrefix") ?? string.Empty;
            byte[] bytes = string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Encode(context)
                                                                                     : CommunicationSupport.Encode(context.Inputs["value"], format);
            TcpFrame.ValidateWrite(bytes, framing);
            int maximum = settings.ReadInt32("maxBytes", 65507);
            if (maximum < bytes.Length)
            {
                return NodeResult.Failure("network.frame.tooLarge", "送信フレーム上限を超えています。", EOutcome.NotExecuted);
            }
            using TcpClient client = new(new IPEndPoint(binding.Resolve(), settings.ReadInt32("localPort")));
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
            await client.ConnectAsync(IPAddress.Parse(settings.ReadText("remoteAddress") ?? string.Empty), settings.ReadInt32("remotePort"), deadline.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            sending = true;
            await TcpFrame.WriteAsync(stream, bytes, framing, deadline.Token).ConfigureAwait(false);
            await CommunicationTrace.WriteAsync(context, "tcp:" + format, true, bytes, CancellationToken.None).ConfigureAwait(false);
            bool awaitReply = settings.ReadBoolean("awaitReply");
            if (!awaitReply)
            {
                return NodeResult.Success(NodeValue.FromObject(new { bytesSent = bytes.Length, confirmation = "localSend" }));
            }
            byte[]? response = await TcpFrame.ReadAsync(stream, framing, maximum, deadline.Token).ConfigureAwait(false);
            if (response is null)
            {
                return NodeResult.Failure("network.response.missing", "返信前に接続が終了しました。", EOutcome.Unknown);
            }
            await CommunicationTrace.WriteAsync(context, "tcp:" + format, false, response, CancellationToken.None).ConfigureAwait(false);
            return string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Decode(response, System.Text.Json.JsonSerializer.SerializeToElement(new { }))!
                                                                             : NodeResult.Success(CommunicationSupport.Decode(response, format));
        }
        catch (Exception exception) when (exception is IOException or SocketException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or System.Text.Json.JsonException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (sending)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("network.tcp.unconfirmed", "TCP処理の完了を確認できません。", outcome);
        }
    }
}
