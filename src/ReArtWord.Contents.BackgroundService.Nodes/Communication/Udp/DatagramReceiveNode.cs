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

/// <summary>UDP待受の寿命とパケット一件の結果を分離します。</summary>
internal sealed class DatagramReceiveNode : ISourceNode
{
    private static string MatchOsc => "osc";

    private readonly string format;

    /// <summary>接続線形式を固定した受信ノードを作成します。</summary>
    internal DatagramReceiveNode(string type, string format)
    {
        this.format = format;
        Descriptor = new(type, type, "Communication", "指定NICでUDPを継続受信します。", ENodeKind.Source, EExecutionLocation.ExplicitPeer,
            [], [new("value", CommunicationSupport.DecodedKind(format))], ["success"], CommunicationSupport.Schema(false, "udp"));
        if (string.Equals(format, MatchOsc, StringComparison.Ordinal))
        {
            Descriptor = Descriptor with { Outputs = [.. Descriptor.Outputs, new("address", EPortValueKind.Json), new("arguments", EPortValueKind.Json)], SettingsSchema = OscNodeValues.Schema(Descriptor.SettingsSchema, false) };
        }
    }

    /// <summary>通信設定を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>待受設定を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return [.. NodeSchemaValidator.Validate(definition, Descriptor), .. CommunicationSupport.Validate(definition, false), .. (string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Validate(definition)
                                                                                                                                                                                            : [])];
    }

    /// <summary>不正パケットを一件失敗として通知し、待受を継続します。</summary>
    /// <returns>待受の終端結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        if (NodeSupport.Invalid(context.Initial, this) is { } invalid)
        {
            return invalid;
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        NodeSettings initial = new(context.Initial.Settings);
        IReadOnlyList<int> ports = ListenPorts.Read(initial.Get("localPort"));
        LocalNetworkBinding binding;
        try
        {
            binding = new(context.Initial.Settings, context.Initial.HostId);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return NodeResult.Failure("network.binding.invalid", "待受の明示NICとIPが不正です。", EOutcome.NotExecuted);
        }
        int count = 0;
        try
        {
            while (!context.IsStopRequested && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using NetworkBindingLease lease = new(binding, lifetime.Token);
                    DatagramListenerSet client = new();
                    await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable subscriptionLifetime = client.ConfigureAwait(false);
                    client.Open(binding.InterfaceId, binding.Resolve(), ports, lease.Token);
                    await context.ReportStatusAsync(new(ESourceState.Listening, null, client.Endpoints), lifetime.Token).ConfigureAwait(false);
                    while (!context.IsStopRequested)
                    {
                        UdpReceiveResult packet = await client.ReadAsync(lease.Token).ConfigureAwait(false);
                        NodeExecutionContext item = context.BeginItem();
                        await CommunicationTrace.WriteAsync(item, format, false, packet.Buffer, CancellationToken.None).ConfigureAwait(false);

                        // NICの変更と定期確認はleaseが監視するため、パケットごとのOS全件照会を重ねません。
                        lease.Token.ThrowIfCancellationRequested();
                        NodeSettings settings = new(item.Settings);
                        NodeResult? result = Decode(packet.Buffer, settings.ReadInt32("maxBytes", 65507), item.Settings);
                        if (result is null)
                        {
                            continue;
                        }
                        await context.EmitAsync(item, result, cancellationToken).ConfigureAwait(false);
                        count++;
                        int limit = settings.ReadInt32("count");
                        if (0 < limit && limit <= count)
                        {
                            return NodeResult.Success();
                        }
                    }
                }
                catch (InvalidOperationException exception) when (exception is not System.Threading.Channels.ChannelClosedException)
                {
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, "network.binding.unresolved", "指定NICまたはIPの再選択が必要です。"), cancellationToken).ConfigureAwait(false);
                    return NodeResult.Failure("network.binding.unresolved", "指定NICまたはIPを解決できません。");
                }
                catch (Exception exception) when (exception is SocketException or IOException or System.Threading.Channels.ChannelClosedException)
                {
                    string code = "network.listen.unavailable";
                    string message = "同じNICとIPで待受を再開します。";
                    if (exception is System.Threading.Channels.ChannelClosedException { InnerException: IOException })
                    {
                        code = "network.receive.overrun";
                        message = "このノードの受信処理が追いつかず、受信キューの上限に達しました。待受を再開します。";
                    }
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, code, message), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return CommunicationSupport.Terminal(cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (lifetime.IsCancellationRequested)
                    {
                        return CommunicationSupport.Terminal(cancellationToken);
                    }
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, "network.binding.unresolved", "同一NICと指定IPの回復を待機します。新IPへ自動変更しません。"), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 待受の復旧処理や状態通知の途中でも、停止要求に対応する終了結果を返します。
        }
        return CommunicationSupport.Terminal(cancellationToken);
    }

    /// <summary>壊れた一件だけを共通失敗へ変換します。</summary>
    /// <returns>取得した一件の結果です。</returns>
    private NodeResult? Decode(byte[] packet, int maximum, JsonElement settings)
    {
        try
        {
            if (maximum < packet.Length)
            {
                throw new FormatException("パケット上限超過です。");
            }
            return string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Decode(packet, settings)
                                                                             : NodeResult.Success(CommunicationSupport.Decode(packet, format));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return NodeResult.Failure("network.packet.invalid", "受信パケットの形式または長さが不正です。");
        }
    }
}
