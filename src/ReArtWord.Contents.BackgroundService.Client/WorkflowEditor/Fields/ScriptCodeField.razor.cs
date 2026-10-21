using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>入力待機・フォーカス離脱時の型検査と、動的言語の未確定型選択です。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class ScriptCodeField
{
    private static readonly string[] typeNames = ["any", "boolean", "byte", "integer", "int64", "number", "decimal", "char", "string", "path", "array", "object"];
    private static IReadOnlyList<string> TypeNames => typeNames;
    private bool busy;
    private bool pending;
    private bool typeRefreshAttempted;
    private string? problem;
    private ElementReference codeInput, highlight;
    private IJSObjectReference? module, editor;
    private readonly SemaphoreSlim rendering = new(1, 1);

    [Inject]
    private IJSRuntime Script
    {
        get; set;
    } = null!;

    private string Language => Node is { } node ? ScriptSettings.Request(node.Type, node.Settings).Language
                                                : string.Empty;
    private bool CanAnalyze => CodeBlockLanguages.SupportsAnalysis(Language);

    /// <summary>コードエディターの言語・編集可否・外部の草稿変更を描画へ同期します。</summary>
    /// <returns>今回の描画との同期です。</returns>
    // 抑制理由: Blazorの固定署名です。画面寿命のトークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await rendering.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            if (ComponentDisposed)
            {
                return;
            }
            module ??= await Script.InvokeAsync<IJSObjectReference>("import", UiCancellation, "./script-code.js").ConfigureAwait(true);
            editor ??= await module.InvokeAsync<IJSObjectReference>("attachCodeEditor", UiCancellation, codeInput, highlight, Language).ConfigureAwait(true);
            await editor.InvokeVoidAsync("update", UiCancellation, Language).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ComponentDisposed)
        {
        }
        finally
        {
            rendering.Release();
        }
        string csharp = "csharp", text = "string";

        // LEGACY-FORMAT: csharp-byte-array-string — 詳細を開いたノードだけを再解析し、旧型情報を草稿へ更新します。
        if (!Inline && !typeRefreshAttempted && !busy && !ComponentDisposed && string.Equals(Language, csharp, StringComparison.Ordinal)
            && Report is { ValueProtocolVersion: 0, Contract: { } contract }
            && (string.Equals(contract.Result.Type, text, StringComparison.Ordinal) || contract.Arguments.Values.Any(type => string.Equals(type.Type, text, StringComparison.Ordinal))))
        {
            typeRefreshAttempted = true;
            await AnalyzeAsync(UiCancellation).ConfigureAwait(true);
            await InvokeAsync(StateHasChanged).ConfigureAwait(true);
        }
    }

    /// <summary>コードエディターの監視・編集状態とJavaScript参照を解放します。</summary>
    /// <returns>画面の所有資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await rendering.WaitAsync().ConfigureAwait(true);
        try
        {
            if (editor is not null)
            {
                await editor.InvokeVoidAsync("dispose").ConfigureAwait(true);
                await editor.DisposeAsync().ConfigureAwait(true);
            }
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            rendering.Release();
            rendering.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    [Inject]
    private EditorScriptAnalysis Analysis
    {
        get; set;
    } = null!;

    /// <summary>対象ノードです。</summary>
    [Parameter, EditorRequired]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>現在のコードです。</summary>
    [Parameter]
    public string Value
    {
        get; set;
    } = string.Empty;

    /// <summary>接続・復元中の編集禁止です。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>グラフ内の小さい表示です。</summary>
    [Parameter]
    public bool Inline
    {
        get; set;
    }

    /// <summary>通常の草稿履歴へコード変更を通知します。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get; set;
    }

    private NodeDefinition? Node => Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, NodeId, StringComparison.Ordinal));
    private JsonObject Settings => JsonNode.Parse(Node?.Settings.GetRawText() ?? "{}")!.AsObject();
    private string ManualJson => Model.ReadInput(NodeId + "/settings/manualContract") ?? Settings["manualContract"]?.ToJsonString() ?? "{}";
    private string? shownFingerprint;
    private ScriptContract? selectionHints;
    private string? selectionContext;
    private ScriptAnalysis? Report => Node is { } node ? ScriptSettings.Latest(node.Type, new Contracts.Values.WorkflowSettingValues(Model.Draft.Document).Resolve(node).Settings)
                                                       : null;

    /// <summary>接続がないときだけ固定値を変換し、排他的な入力方式へ切り替えます。</summary>
    private void ChangeInputMode(ChangeEventArgs args)
    {
        if (Disabled || Model.Locks.Node(Model.Draft.Document.Id, NodeId) || !ScriptInputMode.CanChange(Model.Draft.Document, NodeId))
        {
            return;
        }
        try
        {
            bool individual = string.Equals(args.Value?.ToString(), "individual", StringComparison.Ordinal);
            Model.ChangeNode(NodeId, node => ScriptInputMode.Change(node, individual));
            problem = null;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            problem = "Check the value type and JSON format.";
        }
    }

    /// <summary>手動型の編集中だけ候補を保ち、確定契約や診断としては再利用しません。</summary>
    private ScriptContract? TypeSuggestions
    {
        get
        {
            ScriptAnalysis? current = Report;
            if (current is not null)
            {
                return current.SuggestedContract ?? current.Contract;
            }
            if (string.Equals(selectionContext, SelectionContext(), StringComparison.Ordinal))
            {
                return selectionHints;
            }
            return null;
        }
    }

    /// <summary>コード・実行先・画面世代が変わった候補を型選択へ混入させません。</summary>
    /// <returns>手動型だけを除いた候補の文脈です。</returns>
    private string? SelectionContext()
    {
        if (Node is not { } node)
        {
            return null;
        }
        NodeDefinition effective = new Contracts.Values.WorkflowSettingValues(Model.Draft.Document).Resolve(node);
        ScriptAnalysisRequest request = ScriptSettings.Request(effective.Type, effective.Settings) with { ManualContract = default };
        return ContractJson.Hash(new { NodeId, Model.Generation, Request = request, Peer = Analysis.Peer(node) });
    }

    /// <summary>選択によって旧解析が無効になる前に、同じコードの入力欄だけを保持します。</summary>
    private void RememberTypeSuggestions()
    {
        if (Report is { } report)
        {
            selectionHints = report.SuggestedContract ?? report.Contract;
            selectionContext = SelectionContext();
        }
    }

    /// <summary>コードを書き換えた後に以前の診断文を表示し続けません。</summary>
    protected override void OnParametersSet()
    {
        string fingerprint = ContractJson.Hash(new { Value, Language });
        if (!string.Equals(shownFingerprint, fingerprint, StringComparison.Ordinal))
        {
            shownFingerprint = fingerprint;
            problem = null;
        }
    }

    /// <summary>入力が落ち着いたコードを検査し、現在の型だけをポートへ反映します。</summary>
    /// <returns>同じ草稿にのみ反映する解析です。</returns>
    private async Task AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        if (Model.Locks.Node(Model.Draft.Document.Id, NodeId) || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (busy)
        {
            pending = true;
            return;
        }
        busy = true;
        try
        {
            if (!CanAnalyze)
            {
                problem = null;
                return;
            }
            ScriptAnalysis? report = await Analysis.AnalyzeAsync(NodeId, cancellationToken).ConfigureAwait(true);
            problem = null;
            if (report is { Diagnostics.Count: > 0 })
            {
                problem = report.Diagnostics[0];
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            problem = "Script analysis could not be confirmed. Analyze again before saving.";
        }
        finally
        {
            busy = false;
        }
        if (pending)
        {
            pending = false;
            await AnalyzeAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>前の型選択を維持して一つだけ変更します。</summary>
    private void Choose(string? argument, string? type)
    {
        RememberTypeSuggestions();
        problem = null;
        JsonObject settings = Settings;
        JsonObject manual = settings["manualContract"] as JsonObject ?? new();
        JsonObject arguments = manual["arguments"] as JsonObject ?? new();
        JsonNode? selected = null;
        if (!string.IsNullOrEmpty(type))
        {
            selected = new JsonObject { ["type"] = type };
        }
        if (argument is null)
        {
            manual["result"] = selected;
        }
        else
        {
            arguments[argument] = selected;
        }
        manual["arguments"] = arguments.DeepClone();
        settings["manualContract"] = manual.DeepClone();
        Model.ChangeNode(NodeId, node => node with { Settings = JsonSerializer.SerializeToElement(settings) });
    }

    /// <summary>入れ子の配列・オブジェクトにも既存型定義を指定できます。</summary>
    private void ManualChanged(ChangeEventArgs args)
    {
        RememberTypeSuggestions();
        string raw = args.Value?.ToString() ?? "{}";
        try
        {
            JsonObject settings = Settings;
            settings["manualContract"] = JsonNode.Parse(raw)?.AsObject();
            Model.ChangeNode(NodeId, node => node with { Settings = JsonSerializer.SerializeToElement(settings) });
            Model.SetInput(NodeId + "/settings/manualContract", null);
            problem = null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            Model.SetInput(NodeId + "/settings/manualContract", raw);
            problem = "Check the value type and JSON format.";
        }
    }

    /// <summary>推定と明示選択を区別します。</summary>
    /// <returns>明示した型名か空文字です。</returns>
    private string ManualType(string? argument)
    {
        JsonNode? manual = Settings["manualContract"];
        if (argument is null)
        {
            return manual?["result"]?["type"]?.GetValue<string>() ?? string.Empty;
        }
        return manual?["arguments"]?[argument]?["type"]?.GetValue<string>() ?? string.Empty;
    }
}
