using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>ポート間で返信待ちを直列化せず、同じ件数上限を適用します。</summary>
public sealed class TcpMultiPortReplyTests
{
    /// <summary>一方のワークフロー返信を保留したまま、別ポートの受信と返信を完了できます。</summary>
    /// <returns>両方の実TCP返信と待受の正常終了です。</returns>
    [Fact]
    public async Task PendingReplyDoesNotBlockAnotherPort()
    {
        int[] ports = MultiPortListenerTests.AvailablePair(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CommunicationReplyRegistry replies = new();
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore(), replies);
        ISourceNode node = (ISourceNode)nodes.Single(item => item.Descriptor.Type is "tcp.listen");
        IActionNode replyNode = (IActionNode)nodes.Single(item => item.Descriptor.Type is "tcp.reply");
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { localPort = ports, count = 2, replyMode = "workflow", framing = "line" }));
        NodeSourceContext source = new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> run = node.RunAsync(source, timeout.Token);
        await listening.Task.WaitAsync(timeout.Token);
        using TcpClient first = new(), second = new();
        await first.ConnectAsync(IPAddress.Loopback, ports[0], timeout.Token);
        await second.ConnectAsync(IPAddress.Loopback, ports[1], timeout.Token);
        using NetworkStream firstStream = first.GetStream(), secondStream = second.GetStream();
        await firstStream.WriteAsync(Encoding.UTF8.GetBytes("first\n"), timeout.Token);
        NodeEmission firstInput = await emissions.Reader.ReadAsync(timeout.Token);
        await secondStream.WriteAsync(Encoding.UTF8.GetBytes("second\n"), timeout.Token);
        NodeEmission secondInput = await emissions.Reader.ReadAsync(timeout.Token);
        Assert.Equal(2, replies.PendingCount);
        Assert.False(run.IsCompleted);
        Assert.Equal(EOutcome.Succeeded, (await replyNode.ExecuteAsync(Reply(secondInput, "two"), timeout.Token)).Outcome);
        byte[] buffer = new byte[4];
        await secondStream.ReadExactlyAsync(buffer, timeout.Token);
        Assert.Equal("two\n", Encoding.UTF8.GetString(buffer));
        Assert.Equal(EOutcome.Succeeded, (await replyNode.ExecuteAsync(Reply(firstInput, "one"), timeout.Token)).Outcome);
        await firstStream.ReadExactlyAsync(buffer, timeout.Token);
        Assert.Equal("one\n", Encoding.UTF8.GetString(buffer));
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
        Assert.Equal(0, replies.PendingCount);
    }

    /// <summary>受信した接続ハンドルを同じPeerの返信ノードへ渡します。</summary>
    /// <returns>一回だけの返信文脈です。</returns>
    private static NodeExecutionContext Reply(NodeEmission emission, string value) => emission.Context with
    {
        Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["request"] = emission.Result.Outputs["request"],
            ["value"] = NodeValue.FromObject(value),
        },
    };
}
