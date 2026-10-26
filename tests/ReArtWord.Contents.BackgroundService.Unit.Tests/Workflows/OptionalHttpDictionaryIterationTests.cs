using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>任意リソースの未登録だけをcontinueへ分岐し、二つの辞書の対応を保持します。</summary>
public sealed class OptionalHttpDictionaryIterationTests
{
    /// <summary>404は本文を解析せず省略し、認証・サーバー障害は集約成功にしません。</summary>
    /// <returns>実C#ワーカーと逐次・並列反復による分岐の確認です。</returns>
    [Theory]
    [InlineData(false, 404, false, false)]
    [InlineData(true, 404, false, false)]
    [InlineData(false, 404, true, false)]
    [InlineData(true, 404, true, false)]
    [InlineData(false, 500, false, false)]
    [InlineData(true, 500, false, false)]
    [InlineData(false, 401, false, false)]
    [InlineData(false, 502, false, false)]
    [InlineData(true, 502, false, false)]
    [InlineData(true, 502, false, true)]
    public async Task OnlyMissingResourcesAreSkippedAsync(bool parallel, int status, bool allMissing, bool limited)
    {
        string worker = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            worker += ".exe";
        }
        IActionNode script = (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, worker), []))
            .Single(node => node.Descriptor.Type is "script.csharp");
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new SerializationNode("json", false),
            new KeyValueNode(), new PassNode(), new IfElseNode(), new ConcurrencyLimitNode(), script);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        string code = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "optional-http-status.csx")).ConfigureAwait(true);
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(new("csharp", code)).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        JsonElement settings = JsonSerializer.SerializeToElement(new { code, analysis, inputMode = "individual" });
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        int otherStatus = allMissing ? 404
                                     : 200;
        NodeValue rows = NodeValue.FromObject(new[]
        {
            new { id = "first", statusCode = otherStatus, body = "{\"first\":null,\"second\":\"voice-a\"}" },
            new { id = "missing", statusCode = status, body = "not a success document" },
            new { id = "last", statusCode = otherStatus, body = "{\"first\":null,\"second\":\"voice-c\"}" },
        });
        string loopType = parallel ? IterationNodeTypes.ParallelForEach
                                   : IterationNodeTypes.ForEach;
        WorkflowDefinition definition = new("optional-lookups", "Optional lookups", [
            new("each", loopType, empty, new Dictionary<string, NodeValue> { ["value"] = rows }),
            new("status", "script.csharp", settings), new("choose", "control.if", empty), new("parse", "data.json.parse", empty),
            new("first", "value.keyValue", empty), new("second", "value.keyValue", empty), new("end", IterationNodeTypes.End, empty),
            new("first-result", "control.pass", empty), new("second-result", "control.pass", empty)],
            [new("each", "item/statusCode", "status", "$arguments.statusCode"), new("status", "value", "choose", "condition"),
                new("choose", "trueStart", "parse", "trigger"), new("choose", "falseStart", "end", "continue"), new("each", "item/body", "parse", "value"),
                new("each", "item/id", "first", "key"), new("each", "item/id", "second", "key"),
                new("parse", "value/first", "first", "value"), new("parse", "value/second", "second", "value"),
                new("each", "loop", "end", "loop"), new("first", "value", "end", "value"), new("second", "value", "end", "value2"),
                new("end", "value", "first-result", "value"), new("end", "value2", "second-result", "value")]);
        if (limited)
        {
            fixture.Engine.ConcurrencyLimiter = limiter;
            definition = definition with
            {
                Nodes = [WorkflowFixture.Definition("limit", ConcurrencyNodeTypes.Start,
                    "{\"maxConcurrency\":1,\"waitTimeoutMs\":240000,\"timeoutMode\":\"proceed\"}") with
                {
                    Inputs = new Dictionary<string, NodeValue> { ["value"] = rows },
                }, .. definition.Nodes.Select(node => node with { Inputs = null })],
                Wires = [.. definition.Wires, new("limit", "value", "each", "value")],
            };
        }
        Runtime.Workflows.Compilation.CompiledWorkflow compiled = IterationTests.Compile(fixture, definition);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        if (status != 404)
        {
            Assert.Equal(EOutcome.Failed, report.Outcome);
            Assert.Empty(IterationResultTests.Outputs(report));
            Assert.Equal("iteration.bodyFailed", IterationTests.Loop(report).Targets.Single().Result.ReasonCode);
            if (limited)
            {
                Assert.Equal("concurrency.body", report.Nodes.Single(node => node.NodeId is "limit").Targets.Single().Result.ReasonCode);
                Assert.Equal(0, limiter.Active);
                Assert.Equal(1, limiter.Released);
                NodeValue recoveredRows = NodeValue.FromObject(new[] { new { id = "available", statusCode = 200, body = "{\"first\":null,\"second\":\"voice-a\"}" } });
                ExecutionReport recovered = await fixture.Engine.ExecuteAsync(compiled, "limit", new Dictionary<string, NodeValue> { ["value"] = recoveredRows })
                    .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
                Assert.Equal(0, limiter.Active);
                Assert.Equal(2, limiter.Released);
                Assert.Equal(2, limiter.Requests.Count);
                Assert.All(limiter.Requests, request =>
                {
                    Assert.Equal(1, request.MaximumConcurrency);
                    Assert.Equal(240000, request.WaitTimeoutMilliseconds);
                    Assert.True(request.ProceedOnTimeout);
                });
            }
            return;
        }
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, IterationTests.Loop(report).Iteration!.Started);
        IReadOnlyDictionary<string, NodeValue> output = IterationResultTests.Outputs(report);
        Assert.All(report.Nodes.Where(node => node.NodeId is "first-result" or "second-result"), node => Assert.Equal(EOutcome.Succeeded, node.Outcome));
        if (allMissing)
        {
            Assert.All(output.Values, value =>
            {
                Assert.NotNull(value.DataType?.KeyType);
                Assert.Empty(value.Json.EnumerateObject());
            });
            return;
        }
        Assert.Equal(2, output["value"].Json.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, output["value"].Json.GetProperty("first").ValueKind);
        Assert.Equal(JsonValueKind.Null, output["value"].Json.GetProperty("last").ValueKind);
        Assert.Equal("voice-a", output["value2"].Json.GetProperty("first").GetString());
        Assert.Equal("voice-c", output["value2"].Json.GetProperty("last").GetString());
        Assert.False(output["value2"].Json.TryGetProperty("missing", out _));
    }
}
