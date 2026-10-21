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

/// <summary>ワークフロー応答の所有Peer、drain、期限と送信後不明を検証します。</summary>
public sealed class DynamicReplyTests
{
    /// <summary>返信開始前のHTTP切断はハンドルを失効させ、後着送信を拒否します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task DisconnectedHttpRequestRejectsLateReplyAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using CancellationTokenSource abortClient = new();
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore());
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { replyMode = "workflow", count = 1 }));
        Task<NodeResult> run = Source(nodes, "http.listen").RunAsync(Context(initial, emissions, listening), timeout.Token);
        string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        using HttpClient client = new();
        Task<HttpResponseMessage> request = client.GetAsync(new Uri(url), abortClient.Token);
        NodeEmission input = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        await abortClient.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await run.WaitAsync(timeout.Token).ConfigureAwait(true)).Outcome);
        NodeExecutionContext reply = ReplyContext(input.Result.Outputs["request"], NodeValue.FromObject("late"));
        Assert.Equal(EOutcome.NotExecuted, (await Action(nodes, "http.reply").ExecuteAsync(reply, timeout.Token).ConfigureAwait(true)).Outcome);
    }

    /// <summary>通常停止後も受付済みHTTPへ生成した応答を返します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task HttpWorkflowReplyDrainsAndRejectsWrongOwnerAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using CancellationTokenSource stop = new();
        CommunicationReplyRegistry registry = new();
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore(), registry);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { replyMode = "workflow", count = 1 }));
        NodeSourceContext source = Context(initial, emissions, listening, stop.Token);
        Task<NodeResult> run = Source(nodes, "http.listen").RunAsync(source, timeout.Token);
        string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        using HttpClient client = new();
        Task<HttpResponseMessage> responseTask = client.GetAsync(new Uri(url), timeout.Token);
        NodeEmission input = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext reply = ReplyContext(input.Result.Outputs["request"], NodeValue.FromObject(new { statusCode = 202, body = new { answer = "generated" } }));
        Assert.Equal(EOutcome.NotExecuted, (await Action(nodes, "http.reply").ExecuteAsync(reply with { PeerId = "other-peer" }, timeout.Token).ConfigureAwait(true)).Outcome);
        await stop.CancelAsync().ConfigureAwait(true);
        Assert.False(run.IsCompleted);
        Assert.Equal(EOutcome.Succeeded, (await Action(nodes, "http.reply").ExecuteAsync(reply, timeout.Token).ConfigureAwait(true)).Outcome);
        using HttpResponseMessage response = await responseTask.ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("{\"answer\":\"generated\"}", await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, input.Result.Outcome);
        Assert.False(emissions.Reader.TryRead(out _));
        Assert.Equal(0, registry.PendingCount);
        Assert.Equal(EOutcome.NotExecuted, (await Action(nodes, "http.reply").ExecuteAsync(reply, timeout.Token).ConfigureAwait(true)).Outcome);
    }

    /// <summary>無返信期限切れは504を返し、後着返信を送信しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ExpiredHttpHandleDoesNotRewriteSuccessfulInputAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        CommunicationReplyRegistry registry = new();
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore(), registry);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { replyMode = "workflow", count = 1, timeoutMs = 150 }));
        Task<NodeResult> run = Source(nodes, "http.listen").RunAsync(Context(initial, emissions, listening), timeout.Token);
        string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        using HttpClient client = new();
        Task<HttpResponseMessage> responseTask = client.GetAsync(new Uri(url), timeout.Token);
        NodeEmission input = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        using HttpResponseMessage response = await responseTask.ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        NodeExecutionContext reply = ReplyContext(input.Result.Outputs["request"], NodeValue.FromObject("late"));
        Assert.Equal(EOutcome.NotExecuted, (await Action(nodes, "http.reply").ExecuteAsync(reply, timeout.Token).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, input.Result.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
        Assert.Equal(0, registry.PendingCount);
    }

    /// <summary>取得したフレームの結果から後続TCP replyを生成します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task TcpWorkflowReplyUsesOriginalFramingExactlyOnceAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CommunicationReplyRegistry registry = new();
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore(), registry);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { replyMode = "workflow", framing = "line", count = 1 }));
        Task<NodeResult> run = Source(nodes, "tcp.listen").RunAsync(Context(initial, emissions, listening), timeout.Token);
        IPEndPoint endpoint = IPEndPoint.Parse(await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true));
        using TcpClient client = new(AddressFamily.InterNetwork);
        await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("question\n"), timeout.Token).ConfigureAwait(true);
        NodeEmission input = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal("question", Encoding.UTF8.GetString(input.Result.Outputs["value"].Binary.Span));
        NodeExecutionContext reply = ReplyContext(input.Result.Outputs["request"], NodeValue.FromObject("answer"));
        Assert.Equal(EOutcome.Succeeded, (await Action(nodes, "tcp.reply").ExecuteAsync(reply, timeout.Token).ConfigureAwait(true)).Outcome);
        byte[] response = new byte[7];
        await stream.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(true);
        Assert.Equal("answer\n", Encoding.UTF8.GetString(response));
        Assert.Equal(EOutcome.NotExecuted, (await Action(nodes, "tcp.reply").ExecuteAsync(reply, timeout.Token).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
        Assert.Equal(0, registry.PendingCount);
    }

    /// <summary>ローカル返信が確認できた後の取消で成功を変更しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CancellationAfterConfirmedTcpReplyKeepsSuccessAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using CancellationTokenSource cancelReply = new();
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore());
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { replyMode = "workflow", maxBytes = 16777216, count = 1 }));
        Task<NodeResult> run = Source(nodes, "tcp.listen").RunAsync(Context(initial, emissions, listening), timeout.Token);
        IPEndPoint endpoint = IPEndPoint.Parse(await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true));
        using TcpClient client = new(AddressFamily.InterNetwork);
        client.ReceiveBufferSize = 1024;
        await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0, 0, 0, 1, 42 }, timeout.Token).ConfigureAwait(true);
        NodeEmission input = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext reply = ReplyContext(input.Result.Outputs["request"], NodeValue.FromBytes(new byte[8388608]));
        Task<NodeResult> send = Action(nodes, "tcp.reply").ExecuteAsync(reply, cancelReply.Token).AsTask();
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, timeout.Token).ConfigureAwait(true);
        NodeResult confirmed = await send.ConfigureAwait(true);
        await cancelReply.CancelAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, confirmed.Outcome);
        Assert.Equal(EOutcome.Succeeded, input.Result.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>同じカタログ内からsourceを取得します。</summary>
    /// <returns>sourceです。</returns>
    private static ISourceNode Source(IReadOnlyList<INode> nodes, string type)
    {
        return (ISourceNode)nodes.Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>同じカタログ内からactionを取得します。</summary>
    /// <returns>actionです。</returns>
    private static IActionNode Action(IReadOnlyList<INode> nodes, string type)
    {
        return (IActionNode)nodes.Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>実際のendpointと受信値を試験へ渡します。</summary>
    /// <returns>sourceの文脈です。</returns>
    private static NodeSourceContext Context(NodeExecutionContext initial, Channel<NodeEmission> emissions, TaskCompletionSource<string> listening, CancellationToken stopToken = default)
    {
        return new(initial, () => initial, (item, token) => emissions.Writer.WriteAsync(item, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        }, stopToken);
    }

    /// <summary>要求と返信値を一件へ固定します。</summary>
    /// <returns>replyの実行文脈です。</returns>
    private static NodeExecutionContext ReplyContext(NodeValue request, NodeValue value)
    {
        return SocketNodeTests.Context(System.Text.Json.JsonSerializer.SerializeToElement(new { })) with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["request"] = request,
                ["value"] = value,
            },
        };
    }
}
