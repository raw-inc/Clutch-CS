using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>不要な親更新を省いても、実際の編集と診断はDOMへ反映されることを確認します。</summary>
public sealed class GraphRenderingTests
{
    /// <summary>ショートカットも既存クリップボード・Undo・ロックと文書世代を尊重します。</summary>
    /// <returns>実コンポーネントからの選択範囲編集の検証です。</returns>
    [Fact]
    public async Task ClipboardShortcutsPreserveConnectionsAndRejectStaleDocumentsAsync()
    {
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        WorkflowDefinition document = EditorPresentationCacheTests.Document("{\"age\":10}");
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        using HttpClient http = new();
        ManagementApi api = new(http);
        GraphActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(model);
        services.AddSingleton(new EditorDiagnostics());
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new EditorCollaboration(model, api));
        services.AddSingleton(new EditorNodeActivity(api));
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton(new SettingChoiceProvider(api));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Catalog"] = EditorPresentationCacheTests.Catalog() };
            parameters["SelectionChanged"] = EventCallback.Factory.Create(this, () => activator.Graph!.SetParametersAsync(ParameterView.FromDictionary(parameters)));
            model.Selection.Add(document.Nodes.Select(node => node.Id));
            await renderer.RenderComponentAsync<NodeGraph>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            NodeGraph graph = activator.Graph!;
            WorkflowDefinition beforePreview = model.Draft.Document;
            Assert.True(graph.CanConnectPorts("source", "value", true, "expand", "trigger", 1));
            Assert.True(graph.CanConnectPorts("expand", "trigger", false, "source", "value", 1));
            Assert.False(graph.CanConnectPorts("source", "value", true, "expand", "trigger", 0));
            Assert.False(graph.CanConnectPorts("source", "value", true, "source", "trigger", 1));
            Assert.False(graph.CanConnectPorts("expand", "value", true, "source", "trigger", 1));
            Assert.Same(beforePreview, model.Draft.Document);
            Assert.False(model.CanUndo);
            await graph.ClipboardShortcutAsync(false, 0, 0, 1).ConfigureAwait(true);
            Assert.True(model.Selection.Clipboard.HasContent);
            await graph.ClipboardShortcutAsync(true, 800, 600, 0).ConfigureAwait(true);
            Assert.Equal(ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Hash(document), ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Hash(model.Draft.Document));
            await graph.ClipboardShortcutAsync(true, 800, 600, 1).ConfigureAwait(true);
            Assert.Equal(4, model.Draft.Document.Nodes.Count);
            Assert.Equal(2, model.Draft.Document.Wires.Count);
            Assert.Equal(2, model.Selection.Nodes.Count);
            NodeDefinition[] pasted = model.Draft.Document.Nodes.Where(node => model.Selection.Nodes.Contains(node.Id)).ToArray();
            Assert.Equal(800, pasted.Min(node => node.Position!.X));
            Assert.Equal(600, pasted.Min(node => node.Position!.Y));
            model.Locks.Update([new(Guid.NewGuid(), model.Draft.Document.Id, null)]);
            await graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.False(graph.CanConnectPorts("source", "value", true, "expand", "trigger", 2));
            await graph.ClipboardShortcutAsync(true, 10, 20, 2).ConfigureAwait(true);
            Assert.Equal(4, model.Draft.Document.Nodes.Count);
            model.Locks.Update([]);
            model.Undo();
            Assert.Equal(ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Hash(document), ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Hash(model.Draft.Document));
            model.Selection.Add(document.Nodes.Select(node => node.Id));
            await graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            await graph.InteractPointerAsync(0, 0, 3, "source").ConfigureAwait(true);
            Assert.Equal(2, model.Selection.Nodes.Count);
            await graph.DeleteSelectionAsync("source", null, 2).ConfigureAwait(true);
            Assert.Equal(2, model.Draft.Document.Nodes.Count);
            model.Locks.Update([new(Guid.NewGuid(), model.Draft.Document.Id, ["expand"])]);
            await graph.DeleteSelectionAsync("source", null, 3).ConfigureAwait(true);
            Assert.Equal(2, model.Draft.Document.Nodes.Count);
            model.Locks.Update([]);
            await graph.DeleteSelectionAsync("source", null, 3).ConfigureAwait(true);
            Assert.Empty(model.Draft.Document.Nodes);
            Assert.Empty(model.Draft.Document.Wires);
            model.Undo();
            Assert.Equal(2, model.Draft.Document.Nodes.Count);
            Assert.Single(model.Draft.Document.Wires);
        }).ConfigureAwait(true);
    }

    /// <summary>静止中とポインター共有では表示を変えず、編集・選択・診断・折畳みは表示を更新します。</summary>
    /// <returns>描画と後続更新の確認です。</returns>
    [Fact]
    public async Task StableRefreshRetainsGraphAndChangesStillRender()
    {
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        WorkflowDefinition document = EditorPresentationCacheTests.Document("{\"age\":10}");
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        using HttpClient http = new();
        ManagementApi api = new(http);
        EditorDiagnostics diagnostics = new();
        GraphActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(model);
        services.AddSingleton(diagnostics);
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new EditorCollaboration(model, api));
        services.AddSingleton(new EditorNodeActivity(api));
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton(new SettingChoiceProvider(api));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Catalog"] = EditorPresentationCacheTests.Catalog(), ["ShowDetails"] = true };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<NodeGraph>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            string original = output.ToHtmlString();
            Assert.Contains("data-wire-port=\"value/age\"", original, StringComparison.Ordinal);
            Assert.DoesNotContain("marker-end", original, StringComparison.Ordinal);
            Assert.Contains("animate", original, StringComparison.Ordinal);
            Assert.DoesNotContain("<fieldset disabled", original, StringComparison.Ordinal);
            await activator.Graph!.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            activator.Graph.MovePointer(20, 30, 1);
            Assert.Equal(original, output.ToHtmlString());
            model.SelectedNode = "expand";
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("graph-node selected", output.ToHtmlString(), StringComparison.Ordinal);
            WorkflowDefinition edited = EditorPresentationCacheTests.Document("{\"name\":\"new\"}");
            model.Change(_ => edited);
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            string changed = output.ToHtmlString();
            Assert.Contains("data-wire-port=\"value/name\"", changed, StringComparison.Ordinal);
            Assert.DoesNotContain("data-wire-port=\"value/age\"", changed, StringComparison.Ordinal);
            diagnostics.SetValidation(model.ClusterId, model.Draft.Document, model.Draft.Document, [new("test", "Invalid", "expand")]);
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("has-error", output.ToHtmlString(), StringComparison.Ordinal);
            model.ChangeNode("expand", node => node with { LogicalName = "Shared display name" });
            diagnostics.UpdateDraft(model.ClusterId, model.Draft);
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("Shared display name", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("has-error", output.ToHtmlString(), StringComparison.Ordinal);
            model.Locks.Update([new(Guid.NewGuid(), model.Draft.Document.Id, ["expand"])]);
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("<fieldset disabled", output.ToHtmlString(), StringComparison.Ordinal);
            model.Locks.Update([]);
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.DoesNotContain("<fieldset disabled", output.ToHtmlString(), StringComparison.Ordinal);
            parameters["ShowDetails"] = false;
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.DoesNotContain("node-card-fields", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("node-title", output.ToHtmlString(), StringComparison.Ordinal);
            WorkflowDefinition spaced = EditorPresentationCacheTests.Document("\"path\"");
            model.Change(_ => spaced with { Nodes = [spaced.Nodes[0], spaced.Nodes[1] with { Position = new(100, 500) }] });
            parameters["View"] = new GraphViewOptions { WireShape = EGraphWireShape.Orthogonal };
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            AssertCorridor(output.ToHtmlString(), 168, 568, 0, 100, 500);
            model.Change(_ => spaced with { Nodes = [spaced.Nodes[0] with { Position = new(500, 700) }, spaced.Nodes[1] with { Position = new(100, 100) }] });
            await activator.Graph.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            AssertCorridor(output.ToHtmlString(), 768, 168, 1, 100, 700);
        }).ConfigureAwait(true);
    }

    /// <summary>追加の共通出力でカードが高くなっても、二つのカード間を通ることを確認します。</summary>
    private static void AssertCorridor(string html, int fromY, int toY, int upperNode, int upperTop, int lowerTop)
    {
        string path = FormattableString.Invariant($"d=\"M 790 {fromY} H 822 V (?<y>[0-9.]+) H 68 V {toY} H 100\"");
        System.Text.RegularExpressions.Match wire = System.Text.RegularExpressions.Regex.Match(html, path, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(wire.Success);
        System.Text.RegularExpressions.MatchCollection boxes = System.Text.RegularExpressions.Regex.Matches(html, "class=\"node-box\" width=\"[0-9.]+\" height=\"(?<height>[0-9.]+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        double height = double.Parse(boxes[upperNode].Groups["height"].Value, System.Globalization.CultureInfo.InvariantCulture);
        double corridor = double.Parse(wire.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(corridor, upperTop + height, lowerTop);
    }

    /// <summary>実コンポーネントへ同じ親更新を届けるため、生成したルートだけ保持します。</summary>
    private sealed class GraphActivator : IComponentActivator
    {
        internal NodeGraph? Graph
        {
            get; private set;
        }

        /// <summary>Blazorの通常の生成手順を維持します。</summary>
        /// <returns>未初期化のコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is NodeGraph graph)
            {
                Graph = graph;
            }
            return component;
        }
    }
}
