using Microsoft.AspNetCore.Http;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>全ポートのパス登録が成功した後にHTTP・WebSocketの受付を開始します。</summary>
internal sealed class HttpEndpointSet : IAsyncDisposable
{
    private readonly List<HttpEndpointLease> endpoints = [];
    private bool ready;

    /// <summary>全て確保できた待受URLです。</summary>
    internal string Urls => string.Join(", ", endpoints.Select(endpoint => endpoint.Url));

    /// <summary>呼出側が破棄を保証した集合へ登録し、全ポートの成功後に受付を開始します。</summary>
    /// <returns>指定ポート全体の登録完了です。</returns>
    internal async Task OpenAsync(LocalNetworkBinding binding, IReadOnlyList<int> ports, string path, RequestDelegate handler, CancellationToken cancellationToken = default)
    {
        foreach (int port in ports)
        {
            endpoints.Add(await HttpEndpointLease.OpenAsync(binding.InterfaceId, binding, port, path, http =>
            {
                if (!Volatile.Read(ref ready))
                {
                    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.CompletedTask;
                }
                return handler(http);
            }, cancellationToken).ConfigureAwait(false));
        }
        Volatile.Write(ref ready, true);
    }

    /// <summary>全ポートの新受付を止めてから、受付済み処理の完了と登録解除を待ちます。</summary>
    /// <returns>このノードが所有するパスの解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref ready, false);
        foreach (HttpEndpointLease endpoint in endpoints)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
        endpoints.Clear();
    }
}
