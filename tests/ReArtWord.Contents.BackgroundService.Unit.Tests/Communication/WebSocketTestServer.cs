using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>試験が所有する実WebSocket待受と、その終了をまとめて管理します。</summary>
internal sealed class WebSocketTestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
    internal CancellationTokenSource Stop
    {
        get;
    } = new();

    internal Channel<NodeEmission> Emissions
    {
        get;
    } = Channel.CreateUnbounded<NodeEmission>();

    internal TaskCompletionSource<Uri> Listening
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Acquired
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<NodeResult> Running
    {
        get;
    }

    internal IActionNode Reply
    {
        get;
    }

    internal IActionNode Request
    {
        get;
    }

    internal CancellationToken Token => cancellation.Token;

    /// <summary>製品カタログの返信registryを共有したノードを使用します。</summary>
    internal WebSocketTestServer(object settings)
    {
        IReadOnlyList<INode> nodes = CommunicationNodeRegistration.Create(new TestSecretStore());
        string listen = "websocket.listen", reply = "websocket.reply", request = "websocket.request";
        ISourceNode server = (ISourceNode)nodes.Single(node => string.Equals(node.Descriptor.Type, listen, StringComparison.Ordinal));
        Reply = (IActionNode)nodes.Single(node => string.Equals(node.Descriptor.Type, reply, StringComparison.Ordinal));
        Request = (IActionNode)nodes.Single(node => string.Equals(node.Descriptor.Type, request, StringComparison.Ordinal));
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(settings));
        NodeSourceContext context = new(initial, () =>
        {
            Acquired.TrySetResult();
            return initial;
        }, (emission, token) => Emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                Listening.TrySetResult(new UriBuilder(status.Message!) { Scheme = "ws" }.Uri);
            }
            return ValueTask.CompletedTask;
        }, Stop.Token);
        Running = server.RunAsync(context, Token);
    }

    /// <summary>失敗した試験でも所有する待受と接続を終了します。</summary>
    /// <returns>すべてのソケットの解放完了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名に従い、所有する取消トークンで終了します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。所有する取消トークンで終了します。")]
    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        _ = await Running.ConfigureAwait(false);
        cancellation.Dispose();
        Stop.Dispose();
    }
}
