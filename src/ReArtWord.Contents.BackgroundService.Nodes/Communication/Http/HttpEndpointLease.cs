using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTPとWebSocketの待受を共有し、パスごとの受付と処理中要求を所有します。</summary>
internal sealed class HttpEndpointLease : IAsyncDisposable
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly Dictionary<(string Interface, IPEndPoint Endpoint), Listener> listeners = [];
    private readonly Listener listener;
    private readonly Endpoint endpoint;
    private bool disposed;

    /// <summary>登録済みのパスだけを所有します。</summary>
    private HttpEndpointLease(Listener listener, Endpoint endpoint)
    {
        this.listener = listener;
        this.endpoint = endpoint;
    }

    /// <summary>ポート0の場合も実際に確保したURLを返します。</summary>
    internal string Url => listener.Application.Urls.Single();

    /// <summary>同じ明示NIC・IP・ポートに、重複しないパスを登録します。ポート0は新規に確保します。</summary>
    /// <returns>解除時にこのパスの処理中要求だけを待つ所有権です。</returns>
    // 抑制理由: Hostは共有Listenerへ所有権を移し、起動失敗時または最後のLease解除時に必ず破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "共有Listenerへ所有権を移譲します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "共有Listenerへ所有権を移譲します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW140", Justification = "起動失敗時のみ未移譲のHostを破棄します。")]
    internal static async Task<HttpEndpointLease> OpenAsync(string interfaceId, LocalNetworkBinding binding, int port, string path, RequestDelegate handler, CancellationToken cancellationToken = default)
    {
        IPAddress address = binding.Resolve();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (string Interface, IPEndPoint Endpoint) key = (interfaceId, new(address, port));
            if (port == 0 || !listeners.TryGetValue(key, out Listener? existing))
            {
                WebApplication app = HttpNodeHost.Create(binding, port);
                Listener created = new(app);
                Endpoint registration = created.Add(path, handler);
                app.UseWebSockets();
                app.Run(created.DispatchAsync);
                bool published = false;
                try
                {
                    await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    int actualPort = new Uri(app.Urls.Single()).Port;
                    created.Key = (interfaceId, new(address, actualPort));
                    listeners.Add(created.Key, created);
                    published = true;
                    return new(created, registration);
                }
                finally
                {
                    if (!published)
                    {
                        await app.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            return new(existing, existing.Add(path, handler));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>受付を止めて既存要求を待ち、最後のパスを解放した場合だけHostも終了します。</summary>
    /// <returns>このパスと、その最後のHost所有権の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。受付済み処理はその要求自身の取消・期限で終了します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            listener.Close(endpoint);
        }
        finally
        {
            gate.Release();
        }
        await endpoint.Drained.Task.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (listener.Remove(endpoint))
            {
                listeners.Remove(listener.Key);
                try
                {
                    await listener.Application.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await listener.Application.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>パスの新規受付と実行中要求数を同じロックで管理します。</summary>
    private sealed class Listener(WebApplication application)
    {
        private readonly Lock routesGate = new();
        private readonly Dictionary<string, Endpoint> routes = new(StringComparer.Ordinal);
        internal WebApplication Application => application;
        internal (string Interface, IPEndPoint Endpoint) Key
        {
            get; set;
        }

        /// <summary>HTTPのmethod差やWebSocketとの差で同じパスを二重所有させません。</summary>
        /// <returns>このパスの登録です。</returns>
        internal Endpoint Add(string path, RequestDelegate handler)
        {
            using (routesGate.EnterScope())
            {
                if (routes.ContainsKey(path))
                {
                    throw new HttpEndpointConflictException(path);
                }
                Endpoint endpoint = new(path, handler);
                routes.Add(path, endpoint);
                return endpoint;
            }
        }

        /// <summary>処理中の要求がある間はパスを予約し、新しい受付を拒否します。</summary>
        internal void Close(Endpoint endpoint)
        {
            using (routesGate.EnterScope())
            {
                endpoint.Closing = true;
                if (endpoint.Active == 0)
                {
                    endpoint.Drained.TrySetResult();
                }
            }
        }

        /// <summary>処理完了後のパスを取り除き、Hostが不要かを返します。</summary>
        /// <returns>残るパスがない場合はtrueです。</returns>
        internal bool Remove(Endpoint endpoint)
        {
            using (routesGate.EnterScope())
            {
                routes.Remove(endpoint.Path);
                return routes.Count == 0;
            }
        }

        /// <summary>完全一致したパスにだけ渡し、未知のパスには404を返します。</summary>
        /// <returns>一件のHTTP要求またはWebSocket接続の終了です。</returns>
        // 抑制理由: RequestDelegateの固定署名で、取消はHttpContext.RequestAbortedから伝播します。
        [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "RequestDelegateの固定署名です。")]
        internal async Task DispatchAsync(HttpContext http)
        {
            Endpoint endpoint;
            using (routesGate.EnterScope())
            {
                if (!routes.TryGetValue(http.Request.Path.Value ?? string.Empty, out Endpoint? found))
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (found.Closing)
                {
                    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                endpoint = found;
                endpoint.Active++;
            }
            try
            {
                await endpoint.Handler(http).ConfigureAwait(false);
            }
            finally
            {
                using (routesGate.EnterScope())
                {
                    endpoint.Active--;
                    if (endpoint.Closing && endpoint.Active == 0)
                    {
                        endpoint.Drained.TrySetResult();
                    }
                }
            }
        }
    }

    /// <summary>解除と処理終了の競合を、Listenerのロックの下で扱います。</summary>
    private sealed class Endpoint(string path, RequestDelegate handler)
    {
        internal string Path => path;
        internal RequestDelegate Handler => handler;
        internal bool Closing
        {
            get; set;
        }

        internal int Active
        {
            get; set;
        }

        internal TaskCompletionSource Drained
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
