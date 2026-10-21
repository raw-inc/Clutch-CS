using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>通信失敗の診断を残しながら要求や認証の秘密を出力しない境界を確認します。</summary>
public sealed class AiFailureLoggingTests
{
    /// <summary>HTTP拒否の数値と段階を残し、プロバイダー本文は記録しません。</summary>
    /// <returns>公開ノードの異常結果の検証です。</returns>
    [Theory]
    [InlineData(401, EOutcome.Failed)]
    [InlineData(429, EOutcome.Failed)]
    [InlineData(503, EOutcome.Unknown)]
    public async Task HttpFailurePreservesStatusWithoutResponseOrPromptAsync(int status, EOutcome outcome)
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{\"error\":\"private-response\"}", status)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.openai.responses").ExecuteAsync(
            NodeTestContext.Create(AiGeneratorTests.Settings(server), NodeValue.FromObject("private-prompt")));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("ai.incomplete", result.ReasonCode);
        Assert.Contains("httpStatus=" + status.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
        Assert.Contains("stage=httpResponse", result.Message, StringComparison.Ordinal);
        Assert.Contains("host=127.0.0.1", result.Message, StringComparison.Ordinal);
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-response", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-not-valid", json, StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    /// <summary>接続前の拒否もsocket種別を残し、暗黙の再送や成功への変更はしません。</summary>
    /// <returns>実ソケットを用いたローカル接続失敗の検証です。</returns>
    [Fact]
    public async Task RefusedConnectionIdentifiesSocketFailureAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        using Socket reservation = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        string endpoint = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/private-path?secret=private-query";
        NodeResult result = await AiGeneratorTests.Node(server, files, "ai.openai.responses").ExecuteAsync(
            NodeTestContext.Create(AiGeneratorTests.Settings(server, new Dictionary<string, object> { ["endpoint"] = endpoint }), NodeValue.FromObject("private-prompt")));
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Contains("stage=httpRequest", result.Message, StringComparison.Ordinal);
        Assert.Contains("socketError=ConnectionRefused", result.Message, StringComparison.Ordinal);
        Assert.Contains("localAddress=127.0.0.1", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-query", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", result.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }
}
