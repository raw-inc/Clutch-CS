using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>認証なし・明示認証・旧設定の互換性と、HTTP失敗の診断を実通信で確認します。</summary>
public sealed class HttpRequestAuthenticationTests
{
    /// <summary>認証なしでは秘密保管を読み取らず、旧設定では同じヘッダーを送ります。</summary>
    /// <returns>一回だけ届いた実HTTP要求と応答の確認です。</returns>
    [Theory]
    [InlineData(null, null, "Authorization", "Bearer", null)]
    [InlineData("auto", "", "Authorization", "Bearer", null)]
    [InlineData("none", "unused", "Invalid Header", "unused", null)]
    [InlineData(null, "api", "Authorization", "Bearer", "Authorization: Bearer private-token")]
    [InlineData("auto", "api", "Authorization", "Bearer", "Authorization: Bearer private-token")]
    [InlineData("secret", "api", "Authorization", "Bearer", "Authorization: Bearer private-token")]
    [InlineData("secret", "api", "X-Api-Key", "", "X-Api-Key: private-token")]
    public async Task AuthenticationSelectionControlsOnlyItsOwnHeaderAsync(string? mode, string? key, string header, string prefix, string? expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        RecordingSecrets secrets = new();
        IActionNode node = RequestNode(secrets);
        Dictionary<string, object> values = new(StringComparer.Ordinal)
        {
            ["url"] = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/test",
            ["method"] = "GET", ["secretHeader"] = header, ["secretScheme"] = prefix,
        };
        if (mode is not null)
        {
            values.Add("authentication", mode);
        }
        if (key is not null)
        {
            values.Add("secretId", key);
        }
        JsonElement settings = SocketNodeTests.Settings(values);
        Assert.Empty(node.Validate(new("request", "http.request", settings)));
        Task<NodeResult> pending = node.ExecuteAsync(SocketNodeTests.Context(settings), timeout.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable pendingLifetime = new TestTaskLifetime(pending, timeout).ConfigureAwait(true);
        string request = await RespondAsync(server, 200, timeout.Token).ConfigureAwait(true);
        NodeResult result = await pending.ConfigureAwait(true);
        Assert.True(result.Outcome is EOutcome.Succeeded, result.ReasonCode + ": " + result.Message);
        Assert.StartsWith("GET /test HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Equal(expected is null ? 0
                                      : 1, secrets.Reads);
        if (expected is null)
        {
            Assert.DoesNotContain("private-token", request, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization:", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-Api-Key:", request, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(expected, request, StringComparison.Ordinal);
        }
        Assert.Equal(200, result.Outputs["value"].Json.GetProperty("statusCode").GetInt32());
        Assert.Equal("{\"ok\":true}", result.Outputs["value"].Json.GetProperty("body").GetString());
        Assert.False(server.Pending());
    }

    /// <summary>認証設定の欠落・不正を送信前に拒否し、認証なしの保存値は無視します。</summary>
    /// <returns>通信を開始しない設定検証の確認です。</returns>
    [Theory]
    [InlineData("secret", "", "Authorization", "http.secret.required")]
    [InlineData("secret", "api", "Invalid Header", "http.authentication.header.invalid")]
    [InlineData("secret", "api", "Content-Type", "http.authentication.header.invalid")]
    public async Task InvalidAuthenticationDoesNotOpenAConnectionAsync(string mode, string key, string header, string reason)
    {
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        RecordingSecrets secrets = new();
        IActionNode node = RequestNode(secrets);
        JsonElement settings = SocketNodeTests.Settings(new { authentication = mode, secretId = key, secretHeader = header, url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Contains(node.Validate(new("request", "http.request", settings)), issue => string.Equals(issue.Code, reason, StringComparison.Ordinal));
        NodeResult result = await node.ExecuteAsync(SocketNodeTests.Context(settings)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(0, secrets.Reads);
        Assert.False(server.Pending());
    }

    /// <summary>存在しないNICは認証や相手側の障害と混同せず、送信前の失敗にします。</summary>
    /// <returns>元設定と公開結果を通した診断です。</returns>
    [Fact]
    public async Task MissingInterfaceIdentifiesTheLocalConfigurationAsync()
    {
        IActionNode node = RequestNode(new RecordingSecrets());
        JsonElement settings = SocketNodeTests.Settings(new { url = "http://127.0.0.1:1/", interfaceId = Guid.NewGuid().ToString("D") });
        NodeResult result = await node.ExecuteAsync(SocketNodeTests.Context(settings)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("http.network.addressUnavailable", result.ReasonCode);
        Assert.Contains("NIC", result.Message, StringComparison.Ordinal);
    }

    /// <summary>要求送信後の期限切れと取消を区別し、相手側の実行結果を未実行へ変更しません。</summary>
    /// <returns>実際に受理済みの要求を自動再送しない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptionAfterDispatchRetainsUnknownOutcomeAsync(bool cancel)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        using CancellationTokenSource requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        JsonElement settings = SocketNodeTests.Settings(new { url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture), method = "GET" });
        Task<NodeResult> pending = RequestNode(new RecordingSecrets()).ExecuteAsync(SocketNodeTests.Context(settings), requestLifetime.Token).AsTask();
        try
        {
            using TcpClient accepted = await server.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
            using NetworkStream stream = accepted.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
            Assert.StartsWith("GET / HTTP/", await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true), StringComparison.Ordinal);

            // HTTP要求の到着後も応答を返さず、呼出元取消と通常の操作期限を別々に検証します。
            if (cancel)
            {
                await requestLifetime.CancelAsync().ConfigureAwait(true);
            }
            NodeResult result = await pending.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.False(timeout.IsCancellationRequested);
            Assert.Equal(EOutcome.Unknown, result.Outcome);
            Assert.Equal(cancel ? "http.request.cancelled"
                                : "http.request.timeout", result.ReasonCode);
            Assert.False(server.Pending());
        }
        finally
        {
            await requestLifetime.CancelAsync().ConfigureAwait(true);
            await pending.ConfigureAwait(true);
        }
    }

    /// <summary>401を認証設定の強制と取り違えないよう、実際に受けたHTTPステータスを示します。</summary>
    /// <returns>相手が返した失敗応答の確認です。</returns>
    [Fact]
    public async Task RejectedStatusIncludesActualAndExpectedStatusAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        JsonElement settings = SocketNodeTests.Settings(new { url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture), method = "GET" });
        Task<NodeResult> pending = RequestNode(new RecordingSecrets()).ExecuteAsync(SocketNodeTests.Context(settings), timeout.Token).AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable pendingLifetime = new TestTaskLifetime(pending, timeout).ConfigureAwait(true);
        await RespondAsync(server, 401, timeout.Token).ConfigureAwait(true);
        NodeResult result = await pending.ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("http.response.rejected", result.ReasonCode);
        Assert.Contains("401", result.Message, StringComparison.Ordinal);
        Assert.Contains("200", result.Message, StringComparison.Ordinal);
    }

    /// <summary>標準登録されたHTTPノードを取得します。</summary>
    /// <returns>製品と同じ認証・通信実装です。</returns>
    private static IActionNode RequestNode(ISecretStore secrets)
    {
        string type = "http.request";
        return (IActionNode)CommunicationNodeRegistration.Create(secrets).Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>一つのGETを受け付け、固定本文のHTTP応答を返します。</summary>
    /// <returns>実ソケットで受け取った要求ヘッダーです。</returns>
    private static async Task<string> RespondAsync(TcpListener server, int status, CancellationToken cancellationToken)
    {
        using TcpClient accepted = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = accepted.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
        StringBuilder headers = new();
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
            headers.AppendLine(line);
        }
        byte[] response = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{{\"ok\":true}}"));
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        return headers.ToString();
    }

    /// <summary>認証なしで秘密情報が参照されていないことを観測します。</summary>
    private sealed class RecordingSecrets : ISecretStore
    {
        /// <summary>秘密値の読取り回数です。</summary>
        internal int Reads
        {
            get; private set;
        }

        /// <summary>試験専用のキーを返します。</summary>
        /// <returns>外部の秘密情報を使わない固定値です。</returns>
        public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult<string?>("private-token");
        }

        /// <summary>要求実行が秘密保管を書き換えることを拒否します。</summary>
        /// <returns>完了しない不正な呼出しです。</returns>
        public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
