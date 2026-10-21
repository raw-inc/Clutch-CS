namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実際の型付き送信境界へ制御可能なHTTP応答を返します。</summary>
internal sealed class ClientHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    /// <summary>一件のHTTP要求を試験側へ渡します。</summary>
    /// <returns>呼出元が破棄する応答です。</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
