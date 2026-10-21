using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>型付き管理要求を一度だけ送信します。</summary>
internal sealed class ManagementApi(HttpClient client, ServerClock? clock = null)
{
    private readonly ServerClock serverClock = clock ?? new();

    /// <summary>観測元と同じ時刻軸で鮮度を判断します。</summary>
    internal DateTimeOffset UtcNow => serverClock.UtcNow;
    private static readonly JsonSerializerOptions responseOptions = new(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <summary>キャッシュ内の応答もHTTP応答と同じ部分DTOの読み取り規則を使用します。</summary>
    /// <returns>既知の項目を読み取った応答です。</returns>
    internal static T? ReadResponse<T>(JsonElement value) => value.Deserialize<T>(responseOptions);

    /// <summary>利用者が指定した記録用表示名です。認証には使用しません。</summary>
    public string? DisplayName
    {
        get; set;
    }

    /// <summary>明示選択した発見済みPeerです。nullは未選択です。</summary>
    public string? SelectedPeerId
    {
        get; set;
    }

    /// <summary>Observerで発見して明示選択したクラスタです。</summary>
    public string? SelectedClusterId
    {
        get; set;
    }

    /// <summary>管理APIから実行結果を照会します。</summary>
    /// <returns>取得できた型付き結果です。</returns>
    public Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        return SendAsync<T>(HttpMethod.Get, path, null, SelectedClusterId, SelectedPeerId, null, cancellationToken);
    }

    /// <summary>固定した要求を管理APIへ一度送信します。</summary>
    /// <returns>受付または実処理の結果です。</returns>
    public Task<T?> PostAsync<T>(string path, object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<T>(HttpMethod.Post, path, request, SelectedClusterId, SelectedPeerId, null, cancellationToken);
    }

    /// <summary>明示した更新要求を管理APIへ送ります。</summary>
    /// <returns>HTTPで確認した結果です。</returns>
    public Task<T?> PutAsync<T>(string path, object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<T>(HttpMethod.Put, path, request, SelectedClusterId, SelectedPeerId, null, cancellationToken);
    }

    /// <summary>画面を開いた時点のクラスタへ秘密等の更新を送ります。</summary>
    /// <returns>固定した対象の管理結果です。</returns>
    public Task<T?> PutForPeerAsync<T>(string path, object request, string? peerId, string? clusterId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<T>(HttpMethod.Put, path, request, clusterId, peerId, null, cancellationToken);
    }

    /// <summary>操作開始時に固定したPeerへ照会します。</summary>
    /// <returns>指定Peerの結果です。</returns>
    public Task<T?> GetForPeerAsync<T>(string path, string? peerId, string? clusterId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync<T>(HttpMethod.Get, path, null, clusterId, peerId, null, cancellationToken);
    }

    /// <summary>元要求のPeerへ明示した回復処理を送信します。</summary>
    /// <returns>指定Peerの回復結果です。</returns>
    public Task<T?> PostForPeerAsync<T>(string path, object request, string? peerId, string? clusterId = null, Guid? requestId = null, CancellationToken cancellationToken = default)
    {
        return SendAsync<T>(HttpMethod.Post, path, request, clusterId, peerId, requestId, cancellationToken);
    }

    /// <summary>状態コードを確認し秘密を含む例外本文は画面に出しません。</summary>
    /// <returns>型付きの応答です。</returns>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string? clusterId, string? peerId, Guid? fixedRequestId, CancellationToken cancellationToken = default)
    {
        if (method == HttpMethod.Get && (clusterId is not null || peerId is not null))
        {
            string separator = "?";
            if (path.Contains('?', StringComparison.Ordinal))
            {
                separator = "&";
            }
            path += separator + "_cluster=" + Uri.EscapeDataString(clusterId ?? string.Empty) + "&_peer=" + Uri.EscapeDataString(peerId ?? string.Empty);
        }
        using HttpRequestMessage request = new(method, path);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (OperatingSystem.IsBrowser())
        {
            Microsoft.AspNetCore.Components.WebAssembly.Http.WebAssemblyHttpRequestMessageExtensions.SetBrowserRequestCache(request,
                Microsoft.AspNetCore.Components.WebAssembly.Http.BrowserRequestCache.NoStore);
        }
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(DisplayName.Length, 120);
            request.Headers.Add("X-Contents-Display-Name", DisplayName);
        }
        if (clusterId is not null)
        {
            request.Headers.Add("X-Contents-Cluster", clusterId);
        }
        if (peerId is not null)
        {
            request.Headers.Add("X-Contents-Peer", peerId);
        }
        if (body is not null)
        {
            JsonElement value = JsonSerializer.SerializeToElement(body, ContractJson.Options);
            string operationKey = "operationId";
            Guid requestId = fixedRequestId ?? Guid.NewGuid();
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(operationKey, out JsonElement operation))
            {
                Guid domainId = operation.GetGuid();
                if (fixedRequestId is not null && fixedRequestId != domainId)
                {
                    throw UiError.InvalidOperation("The original operation ID does not match the request body.");
                }
                requestId = domainId;
            }
            request.Headers.Add("X-Contents-Request", requestId.ToString("D"));
            request.Content = JsonContent.Create(body, options: ContractJson.Options);
        }
        long started = serverClock.Timestamp;
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        serverClock.Observe(response.Headers, started);
        if (!response.IsSuccessStatusCode)
        {
            string template = "The management API could not confirm the operation ({0}). Query side-effect requests using the same operation ID.";
            string reason = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, (int)response.StatusCode);
            throw UiError.Attach(new HttpRequestException(reason, null, response.StatusCode), template, (int)response.StatusCode);
        }
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(content, responseOptions);
    }
}
