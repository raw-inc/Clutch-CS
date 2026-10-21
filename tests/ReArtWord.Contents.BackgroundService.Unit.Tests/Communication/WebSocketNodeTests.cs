using System.Net.WebSockets;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>製品の両役割を実WebSocketで接続し、断片と正常停止の境界を検証します。</summary>
public sealed class WebSocketNodeTests
{
    /// <summary>明示NICクライアントと継続サーバーが実メッセージを交換します。</summary>
    /// <returns>取得値と送信値の確認です。</returns>
    [Fact]
    public async Task ClientAndServerExchangeTextOnSelectedInterfaceAsync()
    {
        WebSocketTestServer server = new(new { count = 1, replyMode = "fixed", reply = "response" });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Uri uri = await server.Listening.Task.WaitAsync(server.Token).ConfigureAwait(true);
        NodeExecutionContext request = SocketNodeTests.Context(SocketNodeTests.Settings(new { url = uri.AbsoluteUri }), input: NodeValue.FromObject("request"));
        NodeResult result = await server.Request.ExecuteAsync(request, server.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("response", result.Outputs["value"].GetString());
        NodeEmission observed = await server.Emissions.Reader.ReadAsync(server.Token).ConfigureAwait(true);
        Assert.Equal("request", observed.Result.Outputs["value"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await server.Running.ConfigureAwait(true)).Outcome);
    }

    /// <summary>先頭断片後の正常停止でもメッセージと後続返信を完了できます。</summary>
    /// <returns>正常終了と一度だけの返信の確認です。</returns>
    [Fact]
    public async Task GracefulStopDrainsFragmentedMessageAndWorkflowReplyAsync()
    {
        WebSocketTestServer server = new(new { replyMode = "workflow" });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        using ClientWebSocket client = new();
        await client.ConnectAsync(await server.Listening.Task.WaitAsync(server.Token).ConfigureAwait(true), server.Token).ConfigureAwait(true);
        await client.SendAsync("hel"u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, server.Token).ConfigureAwait(true);
        await server.Acquired.Task.WaitAsync(server.Token).ConfigureAwait(true);
        await server.Stop.CancelAsync().ConfigureAwait(true);
        await client.SendAsync("lo"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, server.Token).ConfigureAwait(true);
        NodeEmission emission = await server.Emissions.Reader.ReadAsync(server.Token).ConfigureAwait(true);
        Assert.Equal("hello", emission.Result.Outputs["value"].GetString());
        NodeExecutionContext reply = NodeTestContext.Create(value: NodeValue.FromBytes(new byte[] { 1, 2, 3 }), extra:
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["request"] = emission.Result.Outputs["request"] });
        Assert.Equal(EOutcome.Succeeded, (await server.Reply.ExecuteAsync(reply, server.Token).ConfigureAwait(true)).Outcome);
        byte[] bytes = new byte[8];
        ValueWebSocketReceiveResult received = await client.ReceiveAsync(bytes.AsMemory(), server.Token).ConfigureAwait(true);
        Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes[..received.Count]);
        Assert.Equal(EOutcome.NotExecuted, (await server.Reply.ExecuteAsync(reply, server.Token).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await server.Running.ConfigureAwait(true)).Outcome);
    }

    /// <summary>一接続の過大メッセージでサーバー全体を停止しません。</summary>
    /// <returns>次接続の正常受信までの確認です。</returns>
    [Fact]
    public async Task OversizedMessageDoesNotStopOtherConnectionsAsync()
    {
        WebSocketTestServer server = new(new { count = 2, maxBytes = 4 });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Uri uri = await server.Listening.Task.WaitAsync(server.Token).ConfigureAwait(true);
        using ClientWebSocket first = new();
        await first.ConnectAsync(uri, server.Token).ConfigureAwait(true);
        await first.SendAsync(Encoding.UTF8.GetBytes("oversized").AsMemory(), WebSocketMessageType.Text, true, server.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, (await server.Emissions.Reader.ReadAsync(server.Token).ConfigureAwait(true)).Result.Outcome);
        using ClientWebSocket second = new();
        await second.ConnectAsync(uri, server.Token).ConfigureAwait(true);
        await second.SendAsync("ok"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, server.Token).ConfigureAwait(true);
        Assert.Equal("ok", (await server.Emissions.Reader.ReadAsync(server.Token).ConfigureAwait(true)).Result.Outputs["value"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await server.Running.ConfigureAwait(true)).Outcome);
    }
}
