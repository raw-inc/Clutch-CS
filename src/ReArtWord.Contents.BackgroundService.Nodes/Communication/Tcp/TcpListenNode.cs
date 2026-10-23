using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Tcp;

/// <summary>TCP接続を受け、明示フレームごとに結果と任意返信を処理します。</summary>
internal sealed class TcpListenNode : ISourceNode
{
    private static string MatchOsc => "osc";

    private readonly string format;
    private readonly TcpItemReader itemReader;

    /// <summary>未加工のまたはOSCの待受を作成します。</summary>
    internal TcpListenNode(string type, string format, CommunicationReplyRegistry replies)
    {
        this.format = format;
        itemReader = new(format, replies);
        Descriptor = new(type, type, "Communication", "指定ポートでTCPを受理し、各フレームの一件結果を通知します。", ENodeKind.Source, EExecutionLocation.ExplicitPeer,
            [], [new("value", CommunicationSupport.DecodedKind(format)), new("request", EPortValueKind.Json)], ["success"], CommunicationSupport.Schema(false, "tcp"));
        if (string.Equals(format, MatchOsc, StringComparison.Ordinal))
        {
            Descriptor = Descriptor with { Outputs = [.. Descriptor.Outputs, new("address", EPortValueKind.Json), new("arguments", EPortValueKind.Json)], SettingsSchema = OscNodeValues.Schema(Descriptor.SettingsSchema, false) };
        }
    }

    /// <summary>設定定義です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>明示NICとフレーム設定を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. NodeSchemaValidator.Validate(definition, Descriptor), .. CommunicationSupport.Validate(definition, false), .. (string.Equals(format, MatchOsc, StringComparison.Ordinal) ? OscNodeValues.Validate(definition)
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

    /// <summary>接続の一件失敗で待受を終了せず次の接続へ進みます。</summary>
    /// <returns>正常終了または取消等の終端結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        if (NodeSupport.Invalid(context.Initial, this) is { } invalid)
        {
            return invalid;
        }
        IReadOnlyList<int> ports = ListenPorts.Read(new NodeSettings(context.Initial.Settings).Get("localPort"));
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        LocalNetworkBinding binding = new(context.Initial.Settings, context.Initial.HostId);
        long received = 0;
        try
        {
            while (!context.IsStopRequested && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunBindingAsync(context, binding, ports, () => Interlocked.Increment(ref received), lifetime.Token, cancellationToken).ConfigureAwait(false);
                    return CommunicationSupport.Terminal(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, "network.binding.unresolved"), cancellationToken).ConfigureAwait(false);
                    return NodeResult.Failure("network.binding.unresolved", "指定NICとIPを再選択してください。");
                }
                catch (SocketException)
                {
                    await context.ReportStatusAsync(new(ESourceState.Unavailable, "network.listen.unavailable"), cancellationToken).ConfigureAwait(false);
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

    /// <summary>全ポートを確保してから受理を開始し、障害時は一式を解放します。</summary>
    /// <returns>この待受世代の全接続処理の終了です。</returns>
    private async Task RunBindingAsync(NodeSourceContext context, LocalNetworkBinding binding, IReadOnlyList<int> ports, Func<long> received,
        CancellationToken lifetimeToken, CancellationToken cancellationToken = default)
    {
        using NetworkBindingLease lease = new(binding, lifetimeToken);
        using CancellationTokenSource accepting = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        Func<NodeExecutionContext, bool> admit = item =>
        {
            // 予定停止前に取得を始めたフレームは完了させ、件数上限だけを全ポートで共有します。
            long total = received();
            int limit = new NodeSettings(item.Settings).ReadInt32("count");
            if (0 < limit && limit <= total)
            {
                accepting.Cancel();
                return total == limit;
            }
            return true;
        };
        List<TcpListener> listeners = [];
        try
        {
            IPAddress address = binding.Resolve();
            foreach (int port in ports)
            {
                accepting.Token.ThrowIfCancellationRequested();
                TcpListener listener = new(address, port);
                listeners.Add(listener);
                listener.Start();
            }
            string endpoints = string.Join(", ", listeners.Select(listener => listener.LocalEndpoint.ToString()));
            await context.ReportStatusAsync(new(ESourceState.Listening, Message: endpoints), accepting.Token).ConfigureAwait(false);
            await Task.WhenAll(listeners.Select(listener => ReadListenerAsync(context, listener, admit, accepting, cancellationToken))).ConfigureAwait(false);
            lease.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            foreach (TcpListener listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    /// <summary>ポートごとに接続を待ち、準備できたフレームを一つの受付件数へ集約します。</summary>
    /// <returns>このポートの受理処理の終了です。</returns>
    private async Task ReadListenerAsync(NodeSourceContext context, TcpListener listener, Func<NodeExecutionContext, bool> admit,
        CancellationTokenSource accepting, CancellationToken cancellationToken = default)
    {
        try
        {
            while (!context.IsStopRequested && !accepting.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(accepting.Token).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                while (!context.IsStopRequested && !accepting.IsCancellationRequested)
                {
                    byte[] firstByte = new byte[1];
                    int available = await client.Client.ReceiveAsync(firstByte, SocketFlags.Peek, accepting.Token).ConfigureAwait(false);
                    if (available == 0)
                    {
                        break;
                    }
                    accepting.Token.ThrowIfCancellationRequested();
                    NodeExecutionContext item = context.BeginItem();
                    TcpItemResult completed = await itemReader.ReadAsync(context, client, item, admit, cancellationToken).ConfigureAwait(false);
                    NodeResult? result = completed.Result;
                    if (result is null)
                    {
                        break;
                    }
                    if (!completed.Emitted)
                    {
                        await context.EmitAsync(item, result, cancellationToken).ConfigureAwait(false);
                    }
                    if (completed.CloseConnection || result.Outcome != EOutcome.Succeeded)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (accepting.IsCancellationRequested)
        {
            // 終了条件・NIC喪失・他ポートの障害で、保留中の受理とPeekだけを起こします。
        }
        finally
        {
            await accepting.CancelAsync().ConfigureAwait(false);
        }
    }
}
