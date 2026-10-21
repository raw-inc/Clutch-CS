using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>ChatとResponsesを公式と同じHTTP形状で試験します。</summary>
public sealed class OpenAiGenerationTests
{
    private static string ChatResult => """{"id":"chat-1","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"answer"}}]}""";
    private static string ResponsesResult => """{"id":"response-1","status":"completed","output":[{"type":"reasoning","summary":[]},{"type":"message","content":[{"type":"output_text","text":"answer"}]}]}""";

    /// <summary>既定URLパスへ入力とmodelだけを送り、任意設定を強制しません。</summary>
    /// <returns>本文と応答JSONを同時に得られます。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultEndpointOmitsUnspecifiedOptionsAsync(bool responses)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(responses ? ResponsesResult
                                                                                                                                                               : ChatResult)))).ConfigureAwait(true);
        string type = Type(responses);
        NodeResult result = await AiGeneratorTests.Node(server, files, type).ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server), NodeValue.FromObject("question"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("answer", result.Outputs["value"].GetString());
        Assert.True(result.Outputs["response"].Json.TryGetProperty("id", out _));
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal(responses ? "/v1/responses"
                               : "/v1/chat/completions", request.Path);
        Assert.Equal("Bearer fixture-secret-not-valid", request.Authorization);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal(2, body.EnumerateObject().Count());
        if (responses)
        {
            Assert.Equal("question", body.GetProperty("input").GetString());
        }
        else
        {
            Assert.Equal("question", body.GetProperty("messages")[0].GetProperty("content").GetString());
        }
    }

    /// <summary>任意設定と構造化メッセージを保持し、完全URLを変更しません。</summary>
    /// <returns>独自パス・クエリへの一回だけの送信です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomEndpointAndStructuredInputRetainApiOptionsAsync(bool responses)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(responses ? ResponsesResult
                                                                                                                                                               : ChatResult)))).ConfigureAwait(true);
        JsonObject settings = JsonNode.Parse(AiGeneratorTests.Settings(server))!.AsObject();
        settings["endpoint"] = new Uri(server.BaseUri, "deployments/text?version=1").AbsoluteUri;
        settings["temperature"] = 0.2;
        settings["store"] = false;
        settings[responses ? "maxOutputTokens"
                           : "maxCompletionTokens"] = 100;
        if (responses)
        {
            settings["instructions"] = "answer concisely";
            settings["previousResponseId"] = "previous-1";
        }
        NodeValue input = NodeValue.FromObject(new[] { new { role = "user", content = "question" } });
        NodeResult result = await AiGeneratorTests.Node(server, files, Type(responses)).ExecuteAsync(NodeTestContext.Create(settings.ToJsonString(), input)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        AiWireRequest request = Assert.Single(server.Requests);
        Assert.Equal("/deployments/text", request.Path);
        Assert.Equal("?version=1", request.Query);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body);
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal(100, body.GetProperty(responses ? "max_output_tokens"
                                                     : "max_completion_tokens").GetInt32());
        Assert.True(JsonElement.DeepEquals(input.Json, body.GetProperty(responses ? "input"
                                                                                  : "messages")));
    }

    /// <summary>Responsesでは保存promptを指定してmodelとinputを省略できます。</summary>
    /// <returns>Chatと異なる必須項目の扱いです。</returns>
    [Fact]
    public async Task ResponsesCanUseSavedPromptWithoutInputOrModelAsync()
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(ResponsesResult)))).ConfigureAwait(true);
        JsonObject settings = JsonNode.Parse(AiGeneratorTests.Settings(server))!.AsObject();
        settings.Remove("model");
        settings["prompt"] = new JsonObject { ["id"] = "pmpt_fixture" };
        NodeResult result = await AiGeneratorTests.Node(server, files, Type(true)).ExecuteAsync(NodeTestContext.Create(settings.ToJsonString())).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(Assert.Single(server.Requests).Body);
        Assert.Single(body.EnumerateObject());
        Assert.Equal("pmpt_fixture", body.GetProperty("prompt").GetProperty("id").GetString());
    }

    /// <summary>拒否、上限による途中終了、終端未確認を成功扱いせず再送しません。</summary>
    /// <returns>外部処理の状態境界です。</returns>
    [Theory]
    [InlineData(false, "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}", EOutcome.Failed)]
    [InlineData(false, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"refused\",\"content\":null}}]}", EOutcome.Failed)]
    [InlineData(false, "{\"choices\":[{\"finish_reason\":null,\"message\":{\"content\":\"partial\"}}]}", EOutcome.Unknown)]
    [InlineData(true, "{\"status\":\"incomplete\",\"output\":[]}", EOutcome.Failed)]
    [InlineData(true, "{\"status\":\"in_progress\",\"output\":[]}", EOutcome.Unknown)]
    [InlineData(true, "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}]}", EOutcome.Failed)]
    public async Task IncompleteOrRefusedResponsesNeverSucceedAsync(bool responses, string response, EOutcome outcome)
    {
        using MediaTestFiles files = new();
        AiWireFixture server;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = (server = new((_, _) => Task.FromResult(AiWireResponse.Json(response)))).ConfigureAwait(true);
        NodeResult result = await AiGeneratorTests.Node(server, files, Type(responses)).ExecuteAsync(NodeTestContext.Create(AiGeneratorTests.Settings(server), NodeValue.FromObject("question"))).ConfigureAwait(true);
        Assert.Equal(outcome, result.Outcome);
        Assert.Single(server.Requests);
    }

    /// <summary>メッセージ組立てを連結し、roleと構造化contentをそのまま保持します。</summary>
    /// <returns>元配列を変更しない追加の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessageBuildersAppendWithoutMutatingInputAsync(bool responses)
    {
        using MediaTestFiles files = new();
        string type = responses ? "ai.openai.responses.message"
                                : "ai.openai.chat.message";
        IActionNode node = (IActionNode)AiNodeRegistration.Create(new AiTestSecrets(), new(), files.Options).Single(item => string.Equals(item.Descriptor.Type, type, StringComparison.Ordinal));
        NodeResult first = await node.ExecuteAsync(NodeTestContext.Create("{\"role\":\"developer\"}", extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["content"] = NodeValue.FromObject("Be concise") })).ConfigureAwait(true);
        NodeValue previous = first.Outputs["value"];
        NodeResult second = await node.ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["messages"] = previous, ["content"] = NodeValue.FromObject("question") })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, second.Outcome);
        Assert.Equal(1, previous.Json.GetArrayLength());
        Assert.Equal(2, second.Outputs["value"].Json.GetArrayLength());
        Assert.Equal("developer", second.Outputs["value"].Json[0].GetProperty("role").GetString());
        Assert.Equal("user", second.Outputs["value"].Json[1].GetProperty("role").GetString());
    }

    /// <summary>旧仮レシピを新規配置一覧へ残さず、保存済み定義の解決だけを維持します。</summary>
    [Fact]
    public void CatalogRemovesPlaceholderGenerators()
    {
        using MediaTestFiles files = new();
        NodeRegistry registry = new(AiNodeRegistration.Create(new AiTestSecrets(), new(), files.Options));
        foreach (string type in new[] { "ai.text.generate", "ai.picture.generate", "ai.video.generate" })
        {
            Assert.DoesNotContain(registry.Descriptors, descriptor => string.Equals(descriptor.Type, type, StringComparison.Ordinal));
            Assert.NotNull(registry.Find(type));
        }
        Assert.Equal("TextGeneration", Assert.Single(registry.Descriptors, descriptor => string.Equals(descriptor.Type, Type(false), StringComparison.Ordinal)).Category);
    }

    /// <summary>ノード識別子だけを切り替えます。</summary>
    /// <returns>公開する独立レシピです。</returns>
    private static string Type(bool responses) => responses switch { true => "ai.openai.responses", false => "ai.openai.chat" };
}
