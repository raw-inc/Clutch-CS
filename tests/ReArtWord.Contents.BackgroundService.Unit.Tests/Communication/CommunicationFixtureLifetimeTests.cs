using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>試験本体が途中で失敗しても、開始した待受の終了とポートの解放を確認します。</summary>
public sealed class CommunicationFixtureLifetimeTests
{
    /// <summary>失敗経路を通った直後に、同じポートを排他的に確保し直せます。</summary>
    /// <returns>TCP・UDP・HTTP共有待受の実資源が残らない確認です。</returns>
    [Theory]
    [InlineData("udp.receive", false)]
    [InlineData("tcp.listen", true)]
    [InlineData("http.listen", true)]
    [InlineData("websocket.listen", true)]
    public async Task FailedTestBodyReleasesItsListenerAsync(string type, bool tcp)
    {
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource startup = new(TimeSpan.FromSeconds(20));
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { }));
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                if (Uri.TryCreate(status.Message, UriKind.Absolute, out Uri? address) && (string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) || string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
                {
                    listening.TrySetResult(address.Port);
                }
                else
                {
                    listening.TrySetResult(IPEndPoint.Parse(status.Message!).Port);
                }
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> running = ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(source, cancellation.Token);
        try
        {
            int port = await listening.Task.WaitAsync(startup.Token).ConfigureAwait(true);
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = new TestTaskLifetime(running, cancellation).ConfigureAwait(true);
                await Task.Yield();
                throw new InvalidOperationException("fixture test body failed");
            }).ConfigureAwait(true);
            Assert.Equal("fixture test body failed", failure.Message);
            SocketType socketType = SocketType.Dgram;
            ProtocolType protocol = ProtocolType.Udp;
            if (tcp)
            {
                socketType = SocketType.Stream;
                protocol = ProtocolType.Tcp;
            }
            using Socket replacement = new(AddressFamily.InterNetwork, socketType, protocol) { ExclusiveAddressUse = true };
            replacement.Bind(new IPEndPoint(IPAddress.Loopback, port));
            Assert.True(running.IsCompleted);
            Assert.Equal(EOutcome.Cancelled, (await running.ConfigureAwait(true)).Outcome);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            _ = await running.ConfigureAwait(true);
        }
    }
}
