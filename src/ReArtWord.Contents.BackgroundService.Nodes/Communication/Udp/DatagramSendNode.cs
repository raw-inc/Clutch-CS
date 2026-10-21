using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Udp;

/// <summary>指定NICから一つのUDPパケットを送ります。</summary>
internal sealed class DatagramSendNode : IActionNode
{
    private static string MatchOsc => "osc";

    private readonly string format;

    /// <summary>接続線形式を固定した送信ノードを作成します。</summary>
    internal DatagramSendNode(string type, string format)
    {
        this.format = format;
        Descriptor = new(type, type, "Communication", "明示したNICからUDPを送信します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
            [new("value", CommunicationSupport.EncodedKind(format), true)], [new("value")], ["success"], CommunicationSupport.Schema(true, "udp")) { HasAsyncCompletion = true };
        if (string.Equals(format, MatchOsc, StringComparison.Ordinal))
        {
            Descriptor = Descriptor with { Inputs = [.. Descriptor.Inputs, new("address", EPortValueKind.Json, OverridesSetting: "address")], SettingsSchema = OscNodeValues.Schema(Descriptor.SettingsSchema, true) };
        }
    }

    /// <summary>通信設定を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>送信先とbind設定を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return [.. NodeSchemaValidator.Validate(definition, Descriptor), .. CommunicationSupport.Validate(definition, true), .. (string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Validate(definition)
                                                                                                                                                                                           : [])];
    }

    /// <summary>パケット送信成立を返し、相手の実動作を保証しません。</summary>
    /// <returns>送信結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        bool sending = false;
        try
        {
            NodeSettings settings = new(context.Settings);
            LocalNetworkBinding binding = new(context.Settings, context.HostId);
            byte[] bytes = string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Encode(context)
                                                                                     : CommunicationSupport.Encode(context.Inputs["value"], format);
            int maximum = settings.ReadInt32("maxBytes", 65507);
            if (65507 < bytes.Length || maximum < bytes.Length)
            {
                return NodeResult.Failure("network.packet.tooLarge", "UDPパケット上限を超えています。", EOutcome.NotExecuted);
            }
            System.Net.NetworkInformation.NetworkInterface network = binding.ResolveInterface(useObservation: OperatingSystem.IsWindows());
            int? interfaceIndex = null;
            if (OperatingSystem.IsWindows())
            {
                interfaceIndex = network.GetIPProperties().GetIPv4Properties().Index;
            }
            DatagramSocketLease client;
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable socketLifetime = (client = DatagramSocketLease.Open(binding.InterfaceId, binding.Resolve(useObservation: OperatingSystem.IsWindows()), settings.ReadInt32("localPort"), receive: false, interfaceIndex)).ConfigureAwait(false);
            IPEndPoint remote = new(IPAddress.Parse(settings.ReadText("remoteAddress") ?? string.Empty), settings.ReadInt32("remotePort"));
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
            sending = true;
            int sent = await client.SendAsync(bytes, remote, settings.ReadBoolean("broadcast"), deadline.Token).ConfigureAwait(false);
            await CommunicationTrace.WriteAsync(context, format, true, bytes, CancellationToken.None).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(new { bytesSent = sent, confirmation = "localSend" }));
        }
        catch (Exception exception) when (exception is SocketException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException or System.Text.Json.JsonException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (sending)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("network.send.unconfirmed", "送信を確認できません。設定・通信状態を確認してください。", outcome);
        }
    }
}
