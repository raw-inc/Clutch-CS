using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>明示NICの一回のAPI処理と送信後の結果不明境界を保持します。</summary>
internal sealed class AiRequestSession : IDisposable
{
    private readonly HttpClient client;
    private readonly SocketsHttpHandler handler;
    private readonly string secret;
    private readonly string? apiKeyHeader;
    private readonly LocalNetworkBinding binding;
    private readonly string localAddress;
    private string phase = "preparation";
    private string? remoteHost;

    /// <summary>接続先と秘密を一回の処理へ固定します。</summary>
    internal AiRequestSession(NodeExecutionContext context, Uri baseUri, string secret, string? apiKeyHeader = null)
    {
        this.secret = secret;
        this.apiKeyHeader = apiKeyHeader;
        binding = new LocalNetworkBinding(context.Settings, context.HostId);
        localAddress = binding.Resolve().ToString();
        handler = HttpTransportFactory.Create(binding);
        client = new HttpClient(handler, false)
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>副作用を伴う要求を送信し始めたかを示します。</summary>
    internal bool Dispatched
    {
        get;
        private set;
    }

    /// <summary>サービス提供元が終端状態を明示したかを示します。</summary>
    internal bool TerminalKnown
    {
        get;
        set;
    }

    /// <summary>再生成せず照会するための受理済みジョブ IDです。</summary>
    internal string? JobId
    {
        get;
        set;
    }

    /// <summary>認証付きJSON要求を送り、応答サイズを制限します。</summary>
    /// <returns>受信済みJSONの呼出元が保持できる値です。</returns>
    internal async Task<JsonElement> JsonAsync(string path, object? body, bool wanAsync = false, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        Authenticate(request);
        if (body is not null)
        {
            request.Method = HttpMethod.Post;
            request.Content = JsonContent.Create(body);
        }
        if (wanAsync)
        {
            request.Headers.Add("X-DashScope-Async", "enable");
        }
        using HttpResponseMessage response = await SendAsync(request, body is not null, cancellationToken).ConfigureAwait(false);
        phase = "responseBody";
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        await CopyBoundedAsync(input, output, 67108864, cancellationToken).ConfigureAwait(false);
        phase = "responseJson";
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>生成音声やダウンロードをメモリへ全保持せず保存します。</summary>
    /// <returns>保存したバイト数です。</returns>
    internal async Task<long> DownloadAsync(Uri uri, object? body, string destination, FileAccessPolicy policy, long maximumBytes, bool authenticate, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (body is not null)
        {
            request.Method = HttpMethod.Post;
            request.Content = JsonContent.Create(body);
        }
        if (authenticate)
        {
            Authenticate(request);
        }
        using HttpResponseMessage response = await SendAsync(request, body is not null, cancellationToken).ConfigureAwait(false);
        phase = "responseBody";
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        phase = "outputFile";
        policy.ValidatePath(destination);
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await CopyBoundedAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return output.Length;
    }

    /// <summary>API文字列が秘密を含んでも結果へそのまま含めません。</summary>
    /// <returns>秘密値を除去した文字列です。</returns>
    internal string Redact(string value)
    {
        return secret.Length == 0 ? value
                                  : value.Replace(secret, "[secret]", StringComparison.Ordinal);
    }

    /// <summary>要求本文や認証情報を含まない通信段階と障害種別を返します。</summary>
    /// <returns>結果不明の説明へ付加する診断です。</returns>
    internal string DescribeFailure(Exception? error)
    {
        return Redact($"stage={phase}; host={remoteHost}; interfaceId={binding.InterfaceId}; localAddress={localAddress}; {AiFailureDiagnostics.Describe(error)}");
    }

    /// <summary>一回のHTTP通信資源を解放します。</summary>
    public void Dispose()
    {
        client.Dispose();
        handler.Dispose();
    }

    /// <summary>一回の処理で固定したサービス提供元の認証方式を適用します。</summary>
    private void Authenticate(HttpRequestMessage request)
    {
        if (secret.Length == 0)
        {
            return;
        }
        if (apiKeyHeader is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return;
        }
        request.Headers.Add(apiKeyHeader, secret);
    }

    /// <summary>応答本文をそのまま返さずHTTPの受理・拒否境界を扱います。</summary>
    /// <returns>呼出側で破棄する応答です。</returns>
    // 抑制理由: 成功応答の所有権を呼出側usingへ移し、転送しない異常応答だけこの場で破棄します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "成功応答は呼出側のusingが所有します。"), SuppressMessage("SourceQuality", "RAW140", Justification = "所有権を移さない異常応答のみ即時破棄します。")]
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool mutation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        phase = "httpRequest";
        remoteHost = new Uri(client.BaseAddress!, request.RequestUri!).DnsSafeHost;
        if (mutation)
        {
            Dispatched = true;
        }
        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        phase = "httpResponse";
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            if (mutation && status is >= 400 and < 500 and not 408)
            {
                TerminalKnown = true;
            }
            response.Dispose();
            throw new HttpRequestException("AI providerが正常応答を返しませんでした。", null, response.StatusCode);
        }
        return response;
    }

    /// <summary>転送サイズを明示上限内に制限します。</summary>
    /// <returns>上限内の転送完了です。</returns>
    internal static async Task CopyBoundedAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (maximumBytes < total)
            {
                throw new InvalidDataException("AI出力が明示上限を超えました。");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}
