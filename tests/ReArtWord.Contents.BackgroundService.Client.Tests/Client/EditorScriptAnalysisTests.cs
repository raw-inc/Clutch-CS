using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>blur解析の適用先と、他の編集内容を保持する境界を確認します。</summary>
public sealed class EditorScriptAnalysisTests
{
    /// <summary>コードブロックのblurで接続先の型も解析し、残っている直接記入を使いません。</summary>
    /// <returns>構文検査と実行契約の両方が現在のコードへ対応することです。</returns>
    [Theory]
    [InlineData("csharp", "public static string Run() => \"new\";")]
    [InlineData("javascript", "export function run() { return 'new'; }")]
    public async Task CodeBlockAnalysisUpdatesConnectedScriptContractAsync(string language, string code)
    {
        List<ScriptAnalysisRequest> requests = [];
        using ClientHttpHandler handler = new(async (message, token) =>
        {
            ScriptAnalysisRequest request = JsonSerializer.Deserialize<ScriptAnalysisRequest>(await message.Content!.ReadAsStringAsync(token).ConfigureAwait(true), ContractJson.Options)!;
            requests.Add(request);
            ScriptContract? contract = null;
            if (!request.SyntaxOnly)
            {
                contract = new(new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal), new("string"));
            }
            ScriptAnalysis report = new(ContractJson.Hash(request), contract, [], PackageEnvironmentId: new string('a', 64));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(report, ContractJson.Options), Encoding.UTF8, "application/json") };
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(client) { SelectedPeerId = "peer" };
        EditorDocument model = await ModelAsync().ConfigureAwait(true);
        model.ChangeNode("script", node => node with { Type = "script." + language });
        NodeDefinition block = new("block", "value.code", JsonSerializer.SerializeToElement(new { language, code }));
        model.Change(document => document with { Nodes = [.. document.Nodes, block], Wires = [new("block", "value", "script", "$settings.code")] });
        EditorScriptAnalysis editor = new(model, api, new(api));
        Assert.NotNull(await editor.AnalyzeAsync("block").ConfigureAwait(true));
        Assert.Equal(2, requests.Count);
        Assert.True(requests[0].SyntaxOnly);
        Assert.False(requests[1].SyntaxOnly);
        Assert.All(requests, request => Assert.Equal(code, request.Code));
        Assert.All(requests, request => Assert.Equal(language, request.Language));
        NodeDefinition script = new WorkflowSettingValues(model.Draft.Document).Resolve(model.Draft.Document.Nodes[0]);
        Assert.Equal("string", ScriptSettings.Confirmed(script.Type, script.Settings)!.Contract!.Result.Type);
        Assert.Equal(new string('a', 64), ScriptSettings.Confirmed(script.Type, script.Settings)!.PackageEnvironmentId);
    }

    /// <summary>解析用内部データを通常の設定欄・新規接続ポートとして表示しません。</summary>
    [Fact]
    public void AnalysisMetadataIsHiddenWithoutRemovingSavedValues()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { code = "source", analysis = new { fingerprint = "saved" } });
        NodeDefinition node = new("script", "script.csharp", settings);
        WorkflowDefinition document = new("flow", "Flow", [node], []);
        NodeDescriptor descriptor = new("script.csharp", "Script", "Scripting", "Script", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [], [], [],
            JsonSerializer.SerializeToElement(new { properties = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["code"] = new { type = "string" },
                ["analysis"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = "object", ["x-script-metadata"] = true },
            } }));
        NodeDescriptor display = EditorNodeDescriptor.Resolve(descriptor, node, document)!;
        Assert.False(display.SettingsSchema.GetProperty("properties").TryGetProperty("analysis", out _));
        Assert.True(node.Settings.TryGetProperty("analysis", out _));
    }

    /// <summary>解析中のコード変更または画面切替で古い結果を破棄します。</summary>
    /// <returns>遅延応答の非適用です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleAnalysisCannotChangeCurrentDocumentAsync(bool navigate)
    {
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new((_, _) => response.Task);
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(client) { SelectedPeerId = "peer", SelectedClusterId = "cluster" };
        EditorDocument model = await ModelAsync().ConfigureAwait(true);
        EditorScriptAnalysis editor = new(model, api, new(api));
        Task<ScriptAnalysis?> pending = editor.AnalyzeAsync("script");
        if (navigate)
        {
            model.New();
        }
        else
        {
            model.ChangeNode("script", node => node with { Settings = JsonSerializer.SerializeToElement(new { code = "public static string Run() => string.Empty;" }) });
        }
        using HttpResponseMessage reply = Reply();
        response.SetResult(reply);
        Assert.Null(await pending.ConfigureAwait(true));
        Assert.All(model.Draft.Document.Nodes, node => Assert.False(node.Settings.TryGetProperty("analysis", out _)));
    }

    /// <summary>解析は型を追加しますが、入力値や配線を黙って削除しません。</summary>
    /// <returns>解析済み型と草稿の保持です。</returns>
    [Fact]
    public async Task AnalysisAddsTypeContractWithoutRemovingInputsAsync()
    {
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(Reply()));
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(client) { SelectedPeerId = "peer" };
        EditorDocument model = await ModelAsync().ConfigureAwait(true);
        EditorScriptAnalysis editor = new(model, api, new(api));
        Assert.NotNull(await editor.AnalyzeAsync("script").ConfigureAwait(true));
        NodeDefinition node = Assert.Single(model.Draft.Document.Nodes);
        Assert.NotNull(node.Inputs?["arguments"]);
        Assert.Equal("integer", ScriptSettings.PortType(node, "value", true)!.Type);
    }

    /// <summary>型解析前の入力値を持つ草稿です。</summary>
    /// <returns>保存版から開始したモデルです。</returns>
    private static async Task<EditorDocument> ModelAsync(CancellationToken cancellationToken = default)
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        NodeDefinition node = new("script", "script.csharp", JsonSerializer.SerializeToElement(new { code = "public static int Run() => 1;" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["arguments"] = NodeValue.FromObject(new { }) });
        WorkflowDefinition definition = new("script-flow", "Script", [node], []);
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto), cancellationToken).ConfigureAwait(true);
        return model;
    }

    /// <summary>現在のコードだけに対応する型解析応答です。</summary>
    /// <returns>管理APIの通常応答です。</returns>
    private static HttpResponseMessage Reply()
    {
        ScriptAnalysis report = new(ContractJson.Hash(new ScriptAnalysisRequest("csharp", "public static int Run() => 1;")), new(new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal), new("integer")), []);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(report, ContractJson.Options), Encoding.UTF8, "application/json") };
    }
}
