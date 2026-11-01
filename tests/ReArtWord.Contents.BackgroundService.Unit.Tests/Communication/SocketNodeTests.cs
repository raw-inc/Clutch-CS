using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>実ソケットで取得境界、フレーム、取消を検証します。</summary>
public sealed class SocketNodeTests
{
    /// <summary>不正OSCの次も待受を続け、取得済み設定世代を保持します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UdpRejectsOneBadPacketAndRetainsAcquisitionRevisionAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonElement settings = Settings(new { count = 2 });
        NodeExecutionContext current = Context(settings, "old");
        NodeSourceContext source = new(current, () =>
        {
            NodeExecutionContext snapshot = current;
            captured.TrySetResult();
            return snapshot;
        }, async (emission, token) =>
        {
            await release.Task.WaitAsync(token).ConfigureAwait(true);
            await emissions.Writer.WriteAsync(emission, token).ConfigureAwait(true);
        }, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        });
        ISourceNode node = (ISourceNode)Find("osc.udp.receive");
        Task<NodeResult> run = node.RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        current = Context(settings, "new");
        using UdpClient client = new(AddressFamily.InterNetwork);
        await client.SendAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(true);
        await captured.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        current = Context(settings, "newest");
        release.TrySetResult();
        NodeEmission first = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, first.Result.Outcome);
        Assert.Equal("new", first.Context.Revision);
        byte[] packet = [47, 120, 0, 0, 44, 0, 0, 0];
        await client.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(true);
        NodeEmission second = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, second.Result.Outcome);
        Assert.Equal("newest", second.Context.Revision);
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>TCPの実送受信をline framingで双方向確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task TcpClientAndServerExchangeExplicitLineFramesAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        NodeExecutionContext initial = Context(Settings(new { framing = "line", reply = "pong", count = 1 }));
        NodeSourceContext source = new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> run = ((ISourceNode)Find("tcp.listen")).RunAsync(source, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        int port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext request = Context(Settings(new { remoteAddress = "127.0.0.1", remotePort = port, framing = "line", awaitReply = true }), input: NodeValue.FromObject("ping"));
        NodeResult response = await ((IActionNode)Find("tcp.request")).ExecuteAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, response.Outcome);
        Assert.Equal("pong", Encoding.UTF8.GetString(response.Outputs["value"].Binary.Span));
        NodeEmission input = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal("ping", Encoding.UTF8.GetString(input.Result.Outputs["value"].Binary.Span));
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>停止トークンを使った待受解除は緊急取消と区別します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceWakeDistinguishesNormalStopAndCancellationAsync(bool emergency)
    {
        using CancellationTokenSource stop = new();
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = Context(Settings(new { }));
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task<NodeResult> run = ((ISourceNode)Find("udp.receive")).RunAsync(source, cancel.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = new TestTaskLifetime(run, cancel).ConfigureAwait(true);
        await listening.Task.WaitAsync(cancel.Token).ConfigureAwait(true);
        EOutcome expected = EOutcome.Succeeded;
        if (emergency)
        {
            expected = EOutcome.Cancelled;
            await cancel.CancelAsync().ConfigureAwait(true);
        }
        else
        {
            await stop.CancelAsync().ConfigureAwait(true);
        }
        Assert.Equal(expected, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>通信カタログから指定能力を取得します。</summary>
    /// <returns>指定したノードです。</returns>
    internal static INode Find(string type)
    {
        return CommunicationNodeRegistration.Create(new TestSecretStore()).Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>実際のループバック NIC識別子を明示した設定を作成します。</summary>
    /// <returns>環境に依存するNIC選択を含んだ設定です。</returns>
    internal static JsonElement Settings(object additional)
    {
        NetworkInterface network = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback)));
        Dictionary<string, object?> settings = new(StringComparer.Ordinal)
        {
            ["interfaceId"] = network.Id,
            ["localAddress"] = "127.0.0.1",
            ["localPort"] = 0,
        };
        foreach (JsonProperty property in JsonSerializer.SerializeToElement(additional).EnumerateObject())
        {
            settings[property.Name] = property.Value;
        }
        return JsonSerializer.SerializeToElement(settings);
    }

    /// <summary>一件の実行文脈を作成します。</summary>
    /// <returns>試験用の文脈です。</returns>
    internal static NodeExecutionContext Context(JsonElement settings, string revision = "one", NodeValue? input = null)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (input is not null)
        {
            inputs["value"] = input;
        }
        return new(Guid.NewGuid(), Guid.NewGuid(), "workflow", revision, "node", "peer", settings, inputs, TimeProvider.System);
    }
}
