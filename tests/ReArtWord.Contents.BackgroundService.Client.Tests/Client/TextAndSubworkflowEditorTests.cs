using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>文字列出力とSubWorkflowの公開型・タブ表示を利用者から観測できる形で確認します。</summary>
public sealed class TextAndSubworkflowEditorTests
{
    /// <summary>パターンとモードの変更から、名前付きの個別出力と配線可否を更新します。</summary>
    [Theory]
    [InlineData("all", "array", false)]
    [InlineData("first", "string", true)]
    public void RegexGroupsExposeNamesAndStableTypes(string mode, string expected, bool compatible)
    {
        NodeDefinition regex = new("regex", "text.regex", JsonSerializer.SerializeToElement(new { mode }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["pattern"] = NodeValue.FromObject(@"(?<name>\w+)-(?<age>\d+)") });
        NodeDefinition substring = new("substring", "text.substring", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition flow = new("flow", "Flow", [regex, substring], []);
        NodeDescriptor[] catalog = [Descriptor(regex.Type, [new("value"), new("pattern")], [new("value"), new("count")]), Descriptor(substring.Type, [new("value")], [new("value")])];
        NodeDescriptor resolved = EditorNodeDescriptor.Resolve(catalog[0], regex, flow)!;
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "value/name", StringComparison.Ordinal));
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "value/age", StringComparison.Ordinal));
        Assert.Equal(expected, new WorkflowTypeInference(flow, catalog).PortType(regex.Id, "value/name", true)!.Type);
        Assert.Equal(compatible, GraphConnectionPolicy.CanConnect(flow, catalog, new("regex", "value/name", "substring", "value")));
    }

    /// <summary>動的レシピにも明示型を適用し、単なるAny入力にはしません。</summary>
    [Fact]
    public void RecipePortsRejectKnownWrongTypes()
    {
        NodeDefinition number = new("number", "value.constant", JsonSerializer.SerializeToElement(new { text = "12", type = "integer" }));
        NodeDefinition recipe = new("call", SubworkflowInterface.CallType("child"), JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition flow = new("parent", "Parent", [number, recipe], []);
        NodeDescriptor[] catalog = [Descriptor(number.Type, [], [new("value")]), Descriptor(recipe.Type, [new("name", Required: true) { DataType = new("string") }], [new("answer") { DataType = new("integer") }])];
        Assert.False(GraphConnectionPolicy.CanConnect(flow, catalog, new("number", "value", "call", "name")));
        Assert.Equal("integer", new WorkflowTypeInference(flow, catalog).PortType(recipe.Id, "answer", true)!.Type);
    }

    /// <summary>編集中の境界追加・削除をタブ名の左の実DOMへ反映します。</summary>
    /// <returns>保存前の判定とアクセシブルな表示の確認です。</returns>
    [Fact]
    public async Task WorkflowTabsRenderKindBeforeTheirNames()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        NodeDefinition input = new("input", SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name = "text", valueType = new { type = "string" } }));
        WorkflowDefinition child = new("child", "Child recipe", [input], []);
        await model.LoadAsync(new(child, "revision", DateTimeOffset.UtcNow, EActivationPolicy.Auto));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton(new ReArtWord.Contents.BackgroundService.Client.Services.BrowserStorage(new BrowserTestRuntime()));
        services.AddSingleton(new EditorDiagnostics());
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EditorWorkflowTabs.SelectedId)] = child.Id,
                [nameof(EditorWorkflowTabs.SelectedName)] = child.Name,
                [nameof(EditorWorkflowTabs.Dirty)] = true,
            });
            string html = (await renderer.RenderComponentAsync<EditorWorkflowTabs>(parameters).ConfigureAwait(true)).ToHtmlString();
            Assert.Contains("aria-label=\"SubWorkflow\"", html, StringComparison.Ordinal);
            Assert.True(html.IndexOf("workflow-kind", StringComparison.Ordinal) < html.IndexOf("Child recipe", StringComparison.Ordinal));
            Assert.True(GraphDeletion.Node(model, input.Id));
            html = (await renderer.RenderComponentAsync<EditorWorkflowTabs>(parameters).ConfigureAwait(true)).ToHtmlString();
            Assert.DoesNotContain("aria-label=\"SubWorkflow\"", html, StringComparison.Ordinal);
        });
    }

    /// <summary>旧一覧より未保存草稿の種別を優先し、他の保存済みタブは最新一覧へ追従します。</summary>
    [Fact]
    public void DraftKindAndSavedKindAreKeptDistinct()
    {
        EditorWorkflowTabState state = new();
        WorkflowListSummary saved = new("child", "Child", "r", "r", DateTimeOffset.UtcNow, 1, false, "hash") { IsSubworkflow = true };
        Assert.True(Assert.Single(state.Update([saved], "other", "Other", false, false), tab => tab.Id is "child").IsSubworkflow);
        Assert.False(Assert.Single(state.Update([saved], "child", "Child", true, false, false), tab => tab.Id is "child").IsSubworkflow);
    }

    /// <summary>描画と配線検証に必要な公開契約を構成します。</summary>
    /// <returns>外部処理を持たない試験用定義です。</returns>
    private static NodeDescriptor Descriptor(string type, PortDescriptor[] inputs, PortDescriptor[] outputs)
        => new(type, type, "Data", type, ENodeKind.Action, EExecutionLocation.Independent, inputs, outputs, ["success"], JsonSerializer.SerializeToElement(new { }));
}
