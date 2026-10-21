using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>Kestrelと明示NICのHTTPクライアントを接続します。</summary>
public sealed class HttpNodeTests
{
    /// <summary>APIキーをそのまま返す応答でも、秘密値を後続へ渡しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task EchoedSecretIsRedactedAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { count = 1, responseBody = "echo:private-token" }));
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find("http.listen")).RunAsync(source, timeout.Token);
        string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext request = SocketNodeTests.Context(SocketNodeTests.Settings(new { url, secretId = "api", secretHeader = "X-Api-Key", secretScheme = "" }));
        string requestType = "http.request";
        IActionNode node = (IActionNode)CommunicationNodeRegistration.Create(new TestSecretStore("private-token")).Single(item => string.Equals(item.Descriptor.Type, requestType, StringComparison.Ordinal));
        NodeResult result = await node.ExecuteAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("echo:[secret]", result.Outputs["value"].Json.GetProperty("body").GetString());
        await run.ConfigureAwait(true);
    }

    /// <summary>RESTとGraphQLの応答確認を実際のHTTP上で行います。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("rest", "{\"ok\":true}", "hello")]
    [InlineData("graphql", "{\"data\":{\"value\":1}}", "{\"query\":\"{ value }\"}")]
    public async Task HttpRequestAndListenerValidateEnvelopeAsync(string mode, string responseBody, string input)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { mode, count = 1, responseBody }));
        NodeSourceContext source = new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find("http.listen")).RunAsync(source, timeout.Token);
        string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeValue value = NodeValue.FromObject(input);
        string graphQl = "graphql";
        if (string.Equals(mode, graphQl, StringComparison.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(input);
            value = NodeValue.FromJson(document.RootElement);
        }
        NodeExecutionContext request = SocketNodeTests.Context(SocketNodeTests.Settings(new { url, mode, success = "body", expectedBody = responseBody }), input: value);
        NodeResult result = await ((IActionNode)SocketNodeTests.Find("http.request")).ExecuteAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(responseBody, result.Outputs["value"].Json.GetProperty("body").GetString());
        NodeEmission emission = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(input, emission.Result.Outputs["value"].Json.GetProperty("body").GetString());
        Assert.Equal(EValueKind.Json, emission.Result.Outputs["value"].Kind);
        Assert.Equal(EPortValueKind.Json, SocketNodeTests.Find("http.listen").Descriptor.Outputs.Single(port => string.Equals(port.Name, "value", StringComparison.Ordinal)).ValueKind);
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
        Assert.False(emissions.Reader.TryRead(out _));
    }

    /// <summary>不一致のHTTP応答は結果不明ではなく確認済み失敗です。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UnexpectedStatusIsConfirmedFailureAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { count = 1, responseStatus = 409 }));
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            if (status.State == ESourceState.Listening)
            {
                listening.TrySetResult(status.Message!);
            }
            return ValueTask.CompletedTask;
        });
        Task<NodeResult> run = ((ISourceNode)SocketNodeTests.Find("http.listen")).RunAsync(source, timeout.Token);
        string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        NodeExecutionContext request = SocketNodeTests.Context(SocketNodeTests.Settings(new { url }));
        NodeResult result = await ((IActionNode)SocketNodeTests.Find("http.request")).ExecuteAsync(request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("http.response.rejected", result.ReasonCode);
        await run.ConfigureAwait(true);
    }
}
