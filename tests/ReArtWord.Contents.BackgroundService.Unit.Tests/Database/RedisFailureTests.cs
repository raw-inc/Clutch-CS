using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Database;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Database;

/// <summary>Redisが実際に受信した後の切断を未実行や自動再試行にしません。</summary>
public sealed class RedisFailureTests
{
    /// <summary>書込み応答が失われても接続を作り直しません。</summary>
    /// <returns>実通信での送信後不明確認です。</returns>
    [Fact]
    public async Task DisconnectAfterReceivingWriteDoesNotReplayAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IActionNode node = (IActionNode)DatabaseNodeRegistration.Create(new TestSecretStore(), new([])).Single(item => string.Equals(item.Descriptor.Type, "database.redis", StringComparison.Ordinal));
        NodeExecutionContext context = NodeTestContext.Create(SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = port, operation = "set" }).GetRawText(), NodeValue.FromObject("payload"),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["key"] = NodeValue.FromObject("key") });
        Task<NodeResult> pending = node.ExecuteAsync(context, deadline.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = new TestTaskLifetime(pending, deadline).ConfigureAwait(true);
        using (TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(true))
        {
            using NetworkStream stream = accepted.GetStream();
            byte[] command = new byte[35];
            await stream.ReadExactlyAsync(command, deadline.Token).ConfigureAwait(true);
            Assert.Equal("*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$7\r\npayload\r\n", System.Text.Encoding.UTF8.GetString(command));
        }
        Assert.Equal(EOutcome.Unknown, (await pending.ConfigureAwait(true)).Outcome);
        Assert.False(listener.Pending());
    }
}
