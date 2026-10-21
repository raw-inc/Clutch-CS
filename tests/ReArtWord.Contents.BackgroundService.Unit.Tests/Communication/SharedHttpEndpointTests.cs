using System.Net;
using System.Net.WebSockets;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>HTTPとWebSocketの実通信で、共有待受とパス単位の解放を検証します。</summary>
public sealed class SharedHttpEndpointTests
{
    /// <summary>同一ポートのHTTPとWebSocketを分離し、重複拒否と一方停止後の継続を確認します。</summary>
    /// <returns>要求が正しいノードだけへ届いた結果です。</returns>
    [Fact]
    public async Task PathsSharePortAndRejectDuplicatesWithoutStoppingOtherOwnersAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource stopFirst = new();
        TaskCompletionSource<string> firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = Start("http.listen", new { path = "/first", responseBody = "first" }, firstReady, stopFirst.Token, timeout.Token);
        List<Task<NodeResult>> running = [first];
        try
        {
            Uri url = new(await firstReady.Task.WaitAsync(timeout.Token).ConfigureAwait(true));
            TaskCompletionSource<string> secondReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<NodeResult> second = Start("http.listen", new { localPort = url.Port, path = "/second", responseBody = "second" }, secondReady, default, timeout.Token);
            running.Add(second);
            await secondReady.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            TaskCompletionSource<string> socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<NodeResult> socketRun = Start("websocket.listen", new { localPort = url.Port, path = "/socket", count = 1, replyMode = "fixed", reply = "pong" }, socketReady, default, timeout.Token);
            running.Add(socketRun);
            await socketReady.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Task<NodeResult> duplicate = Start("websocket.listen", new { localPort = url.Port, path = "/first" }, new(TaskCreationOptions.RunContinuationsAsynchronously), default, timeout.Token);
            running.Add(duplicate);
            Assert.Equal("http.endpoint.duplicate", (await duplicate.WaitAsync(timeout.Token).ConfigureAwait(true)).ReasonCode);
            using HttpClient client = new();
            Assert.Equal("first", await client.GetStringAsync(new Uri(url, "/first"), timeout.Token).ConfigureAwait(true));
            Assert.Equal("second", await client.GetStringAsync(new Uri(url, "/second"), timeout.Token).ConfigureAwait(true));
            using HttpResponseMessage missing = await client.GetAsync(new Uri(url, "/missing"), timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using ClientWebSocket socket = new();
            await socket.ConnectAsync(new UriBuilder(url) { Scheme = "ws", Path = "/socket" }.Uri, timeout.Token).ConfigureAwait(true);
            await socket.SendAsync("ping"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(true);
            byte[] buffer = new byte[16];
            ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(true);
            Assert.Equal("pong", Encoding.UTF8.GetString(buffer, 0, received.Count));
            await socketRun.WaitAsync(timeout.Token).ConfigureAwait(true);
            await stopFirst.CancelAsync().ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, (await first.WaitAsync(timeout.Token).ConfigureAwait(true)).Outcome);
            Assert.Equal("second", await client.GetStringAsync(new Uri(url, "/second"), timeout.Token).ConfigureAwait(true));
            TaskCompletionSource<string> replacementReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Add(Start("http.listen", new { localPort = url.Port, path = "/first", responseBody = "replacement" }, replacementReady, default, timeout.Token));
            await replacementReady.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal("replacement", await client.GetStringAsync(new Uri(url, "/first"), timeout.Token).ConfigureAwait(true));
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            await Task.WhenAll(running).ConfigureAwait(true);
        }
    }

    /// <summary>実際の待受通知を待ち、時間依存の開始待ちを避けます。</summary>
    /// <returns>呼出側が必ず終了を待つSourceのタスクです。</returns>
    private static Task<NodeResult> Start(string type, object settings, TaskCompletionSource<string> ready, CancellationToken stop, CancellationToken cancellationToken = default)
    {
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(settings));
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                ready.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        }, stop);
        return ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(source, cancellationToken);
    }
}
