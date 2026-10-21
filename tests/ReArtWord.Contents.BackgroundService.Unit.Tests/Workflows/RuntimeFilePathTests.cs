using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>文字列で作った操作パスを、型検査から実ファイル操作まで接続します。</summary>
public sealed class RuntimeFilePathTests
{
    /// <summary>実行時のPathをCommentで二度中継しても、結合と実ファイル確認へ渡せます。</summary>
    /// <returns>型情報と結合結果を維持した実グラフの結果です。</returns>
    [Fact]
    public async Task CommentsKeepDynamicPathsBeforeAndAfterConcat()
    {
        using FileTestDirectory files = new();
        Directory.CreateDirectory(Path.Combine(files.Root, "present"));
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(PathValues.Create(files.Root))));
        WorkflowFixture fixture = new([source, new CommentNode(), new ConcatNode(), .. files.Nodes]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        NodeDefinition comment = new("before", "value.comment", JsonSerializer.SerializeToElement(new { comment = "Path" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["displayName"] = NodeValue.FromObject("Path alias") });
        NodeDefinition concat = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input2"] = PathValues.Create("present") });
        WorkflowDefinition definition = new("comment-path", "Comment Path", [WorkflowFixture.Definition("source"), comment, concat, comment with { Id = "after" }, Target("directory.exists", new { })],
            [new("source", "value", "before", "value"), new("before", "value", "concat", "input1"),
                new("concat", "value", "after", "value"), new("after", "value", "target", SettingPorts.Name("path"))]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source").ConfigureAwait(true);
        foreach (string id in new[] { "before", "concat", "after" })
        {
            NodeResult result = Assert.Single(report.Nodes.Single(node => string.Equals(node.NodeId, id, StringComparison.Ordinal)).Targets).Result;
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal("path", result.Outputs["value"].DataType!.Type);
        }
        NodeResult exists = Assert.Single(report.Nodes.Single(node => node.NodeId is "target").Targets).Result;
        Assert.Equal(EOutcome.Succeeded, exists.Outcome);
        Assert.True(exists.Outputs["value"].Json.GetBoolean());
    }

    /// <summary>実行中に結合した文字列を使い、保存版の空欄や古い値へ戻りません。</summary>
    /// <returns>実ランタイムと実ファイル存在確認の結果です。</returns>
    [Theory]
    [InlineData("directory.exists")]
    [InlineData("file.exists")]
    public async Task DynamicStringThroughConcatReachesExistenceCheck(string operation)
    {
        using FileTestDirectory files = new();
        string path = Path.Combine(files.Root, "present");
        string directoryExists = "directory.exists";
        if (string.Equals(operation, directoryExists, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, "present").ConfigureAwait(true);
        }
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(files.Root))));
        WorkflowFixture fixture = new([source, new ConcatNode(), .. files.Nodes]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        NodeDefinition concat = new("concat", ConcatPorts.NodeType, JsonSerializer.SerializeToElement(new { }), Inputs:
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { [ConcatPorts.Name(2)] = NodeValue.FromObject(Path.DirectorySeparatorChar + "present") });
        NodeDefinition target = Target(operation, new { path = "" });
        WorkflowDefinition definition = new("w", "dynamic path", [WorkflowFixture.Definition("source"), concat, target],
            [new("source", "value", "concat", ConcatPorts.Name(1)), new("concat", "value", "target", SettingPorts.Name("path"))]);
        Assert.All(definition.Wires, wire => Assert.True(WorkflowConnectionTypes.CanConnect(definition, fixture.Registry.Descriptors, wire)));
        CompilationResult compilation = fixture.Compiler.Compile(definition);
        Assert.Empty(compilation.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compilation.Workflow!, "source").ConfigureAwait(true);
        NodeResult result = Assert.Single(Assert.Single(report.Nodes, node => node.NodeId is "target").Targets).Result;
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.True(result.Outputs["value"].Json.GetBoolean());
        Assert.Equal("", definition.Nodes[2].Settings.GetProperty("path").GetString());
    }

    /// <summary>両パスを実行結果から取り出し、同じ文字列契約でコピーと転送を実行します。</summary>
    /// <returns>保存値ではなく接続値を使った実コピーです。</returns>
    [Theory]
    [InlineData("file.copy", "path", "destination")]
    [InlineData("file.transfer", "sourcePath", "path")]
    public async Task CopyAndTransferResolveBothPaths(string operation, string sourceProperty, string targetProperty)
    {
        using FileTestDirectory files = new();
        string from = Path.Combine(files.Root, "source.txt"), to = Path.Combine(files.Root, "target.txt");
        await File.WriteAllTextAsync(from, "copied").ConfigureAwait(true);
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(new { from, to }))));
        WorkflowFixture fixture = new([source, .. files.Nodes]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        string transfer = "file.transfer";
        object settings = string.Equals(operation, transfer, StringComparison.Ordinal) ? new { sourcePeer = "a" }
                                                                                       : new { };
        WorkflowDefinition definition = new("w", "copy", [WorkflowFixture.Definition("source"), Target(operation, settings)],
            [new("source", "value", "target", SettingPorts.Name(sourceProperty), Path: "/from"), new("source", "value", "target", SettingPorts.Name(targetProperty), Path: "/to")]);
        CompilationResult compilation = fixture.Compiler.Compile(definition);
        Assert.Empty(compilation.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compilation.Workflow!, "source").ConfigureAwait(true);
        Assert.True(File.Exists(to), JsonSerializer.Serialize(report));
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal("copied", await File.ReadAllTextAsync(to).ConfigureAwait(true));
    }

    /// <summary>他の入力が起動しても未供給・不正なパスで保存値を使って削除しません。</summary>
    /// <returns>保存パスが無傷で残ることです。</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("empty")]
    [InlineData("binary")]
    [InlineData("route")]
    [InlineData("outside")]
    public async Task InvalidDynamicPathNeverFallsBackToStoredPath(string mode)
    {
        using FileTestDirectory files = new();
        using FileTestDirectory outside = new();
        string saved = Path.Combine(files.Root, "saved.txt"), external = Path.Combine(outside.Root, "external.txt");
        await File.WriteAllTextAsync(saved, "keep").ConfigureAwait(true);
        await File.WriteAllTextAsync(external, "keep").ConfigureAwait(true);
        NodeResult supplied = mode switch
        {
            "missing" => NodeResult.Success(),
            "null" => NodeResult.Success(NodeValue.Null),
            "number" => NodeResult.Success(NodeValue.FromObject(12)),
            "empty" => NodeResult.Success(NodeValue.FromObject("")),
            "binary" => NodeResult.Success(NodeValue.FromBytes(new byte[] { 1 })),
            "outside" => NodeResult.Success(NodeValue.FromObject(external)),
            _ => NodeResult.Success(NodeValue.FromObject(saved)),
        };
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(supplied));
        WorkflowFixture fixture = new([source, .. files.Nodes]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Executor.FileExecutionHandler = static (node, context, token) => node.ExecuteAsync(context, token);
        string rejectedRoute = "route";
        string route = string.Equals(mode, rejectedRoute, StringComparison.Ordinal) ? "failure"
                                                                                    : "success";
        WorkflowDefinition definition = new("w", "guard", [WorkflowFixture.Definition("source"), Target("file.delete", new { path = saved })],
            [new("source", "value", "target", SettingPorts.Name("path"), route), new("source", "result", "target", "trigger")]);
        CompilationResult compilation = fixture.Compiler.Compile(definition);
        Assert.Empty(compilation.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compilation.Workflow!, "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => node.NodeId is "target").Outcome);
        Assert.Equal("keep", await File.ReadAllTextAsync(saved).ConfigureAwait(true));
        Assert.Equal("keep", await File.ReadAllTextAsync(external).ConfigureAwait(true));
    }

    /// <summary>関連する有限操作の全パスへ型判定を水平展開し、待受の構成は静的に保ちます。</summary>
    [Fact]
    public void AllFileOperationPathsAcceptDynamicStringsButRejectKnownNumbers()
    {
        using FileTestDirectory files = new();
        WorkflowFixture.ActionNode source = new((_, _) => ValueTask.FromResult(NodeResult.Success()));
        NodeRegistry registry = new([source, new ConstantValueNode(), .. files.Nodes]);
        foreach (NodeDescriptor descriptor in registry.Descriptors.Where(node => node.Category is "File system"))
        {
            foreach (JsonProperty property in descriptor.SettingsSchema.GetProperty("properties").EnumerateObject().Where(property => property.Name is "path" or "sourcePath" or "destination"))
            {
                WireDefinition wire = new("source", "value", "target", SettingPorts.Name(property.Name));
                WorkflowDefinition document = new("w", "types", [WorkflowFixture.Definition("source"), Target(descriptor.Type, new { })], [wire]);
                Assert.Equal(descriptor.Kind == ENodeKind.Action, WorkflowConnectionTypes.CanConnect(document, registry.Descriptors, wire));
                NodeDefinition number = new("source", "value.constant", JsonSerializer.SerializeToElement(new { text = "123", type = "integer" }));
                Assert.False(WorkflowConnectionTypes.CanConnect(document with { Nodes = [number, document.Nodes[1]] }, registry.Descriptors, wire));
                NodeDefinition literal = number with { Settings = JsonSerializer.SerializeToElement(new { text = files.Root, type = "string" }) };
                Assert.True(WorkflowConnectionTypes.CanConnect(document with { Nodes = [literal, document.Nodes[1]] }, registry.Descriptors, wire));
            }
        }
    }

    /// <summary>静的パスの既存配線は設定版へ解決され、未接続の必須欄は引き続き拒否されます。</summary>
    [Fact]
    public void StaticBindingsAndUnconnectedValidationArePreserved()
    {
        using FileTestDirectory files = new();
        WorkflowCompiler compiler = new(new NodeRegistry([new ConstantValueNode(), .. files.Nodes]));
        NodeDefinition literal = new("source", "value.constant", JsonSerializer.SerializeToElement(new { text = files.Root, type = "string" }));
        WorkflowDefinition definition = new("w", "static", [literal, Target("directory.exists", new { path = "" })], [new("source", "value", "target", SettingPorts.Name("path"))]);
        CompilationResult compilation = compiler.Compile(definition);
        Assert.Empty(compilation.Issues);
        Assert.Empty(compilation.Workflow!.Incoming["target"]);
        Assert.Equal(files.Root, compilation.Workflow.FindNode("target")!.Settings.GetProperty("path").GetString());
        Assert.Null(compiler.Compile(definition with { Wires = [] }).Workflow);
    }

    /// <summary>検証専用のローカルPeerを明示します。</summary>
    /// <returns>操作定義です。</returns>
    private static NodeDefinition Target(string operation, object settings) => new("target", operation, JsonSerializer.SerializeToElement(settings),
        Target: new(EExecutionScope.All, ["a"]));
}
