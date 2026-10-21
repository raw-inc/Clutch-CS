using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>実HTTPで、指定メソッド・照会変換・JSON本文型とdecimal精度を確認します。</summary>
public sealed class HttpTypedInputTests
{
    /// <summary>文字列照会だけを明示変換し、本文の文字列や重複スカラーは受け付けません。</summary>
    /// <returns>実待受と実要求を終了するまでの検証です。</returns>
    [Fact]
    public async Task MethodAndTypedParametersAreAppliedToRealRequestsAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource stop = new();
        TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        NodeDefinition methodDefinition = new("method", "http.method", JsonSerializer.SerializeToElement(new { method = "POST" }));
        NodeResult method = new HttpMethodNode().Evaluate(methodDefinition, new Dictionary<string, NodeValue>(StringComparer.Ordinal));
        ValueTypeDefinition queryType = new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal)
        {
            ["age"] = new("integer"),
            ["tags"] = new("array", new("string")),
            ["nickname"] = new("string", Optional: true),
            ["amount"] = new("decimal", Optional: true),
        });
        ValueTypeDefinition bodyType = new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal)
        {
            ["price"] = new("decimal"),
            ["label"] = new("char", Optional: true),
        });
        NodeExecutionContext initial = SocketNodeTests.Context(SocketNodeTests.Settings(new { })) with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["method"] = method.Outputs["value"],
                ["queryType"] = NodeValue.FromObject(queryType),
                ["bodyType"] = NodeValue.FromObject(bodyType),
            },
        };
        NodeSourceContext context = new(initial, () => initial, (emission, token) => emissions.Writer.WriteAsync(emission, token),
            () => stop.IsCancellationRequested, (status, _) =>
            {
                if (status.State == ESourceState.Listening)
                {
                    listening.TrySetResult(status.Message!);
                }
                return ValueTask.CompletedTask;
            }, stop.Token);
        Task<NodeResult> running = ((ISourceNode)SocketNodeTests.Find("http.listen")).RunAsync(context, timeout.Token);
        try
        {
            string url = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            using HttpClient client = new();
            using HttpResponseMessage wrongMethod = await client.GetAsync(new Uri(url + "?age=25&tags=a"), timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
            Assert.Equal("POST", Assert.Single(wrongMethod.Content.Headers.Allow));
            Assert.False(emissions.Reader.TryRead(out _));

            string exact = "0.1234567890123456789012345678";
            using StringContent validBody = new("{\"price\":" + exact + "}", Encoding.UTF8, "application/json");
            using HttpResponseMessage valid = await client.PostAsync(new Uri(url + "?age=25&tags=a&tags=b"), validBody, timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            NodeEmission received = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, received.Result.Outcome);
            Assert.Equal(25, received.Result.Outputs["query"].Json.GetProperty("age").GetInt64());
            Assert.Equal(2, received.Result.Outputs["query"].Json.GetProperty("tags").GetArrayLength());
            Assert.Equal(exact, received.Result.Outputs["body"].Json.GetProperty("price").GetRawText());

            (string Query, string Body)[] invalid =
            [
                ("age=1&age=2&tags=a", "{\"price\":1}"),
                ("age=1.5&tags=a", "{\"price\":1}"),
                ("tags=a", "{\"price\":1}"),
                ("age=25&tags=a&extra=1", "{\"price\":1}"),
                ("age=25&tags=a", "{\"price\":\"1\"}"),
                ("age=25&tags=a", "{\"price\":0.12345678901234567890123456789}"),
                ("age=25&tags=a&amount=1.0e-2147483648", "{\"price\":1}"),
            ];
            foreach ((string query, string body) in invalid)
            {
                using StringContent data = new(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage rejected = await client.PostAsync(new Uri(url + "?" + query), data, timeout.Token).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                NodeEmission failure = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
                Assert.Equal(EOutcome.Failed, failure.Result.Outcome);
                Assert.Equal("http.request.invalid", failure.Result.ReasonCode);
            }
            byte[] malformed = [.. Encoding.UTF8.GetBytes("{\"price\":1,\"label\":\""), 0xFF, .. Encoding.UTF8.GetBytes("\"}")];
            using ByteArrayContent malformedBody = new(malformed);
            using HttpResponseMessage invalidUtf8 = await client.PostAsync(new Uri(url + "?age=25&tags=a"), malformedBody, timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, invalidUtf8.StatusCode);
            NodeEmission invalidEncoding = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Failed, invalidEncoding.Result.Outcome);
            Assert.Equal("http.request.invalid", invalidEncoding.Result.ReasonCode);

            using StringContent recoveredBody = new("{\"price\":1.25,\"label\":\"😀\"}", Encoding.UTF8, "application/json");
            using HttpResponseMessage recovered = await client.PostAsync(new Uri(url + "?age=26&tags=ok&amount=0.0e-2147483648"), recoveredBody, timeout.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            NodeEmission resumed = await emissions.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, resumed.Result.Outcome);
            Assert.Equal(0m, resumed.Result.Outputs["query"].Json.GetProperty("amount").GetDecimal());
            Assert.Equal("😀", resumed.Result.Outputs["body"].Json.GetProperty("label").GetString());
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(true);
            _ = await running.ConfigureAwait(true);
        }
    }
}
