using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Catalog;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Pages;

/// <summary>草稿のノード配置・検証・取込と書出しを管理します。</summary>
public sealed partial class WorkflowEditorPage
{
    private bool recipePreviewOpen;
    private string recipePreviewNode = string.Empty, recipePreviewWorkflow = string.Empty, recipePreviewCluster = string.Empty;

    /// <summary>編集中の定義を切り替えず、実効配置を別画面で表示します。</summary>
    private void PreviewRecipe(string node)
    {
        if (Model.ClusterId is null)
        {
            return;
        }
        recipePreviewNode = node;
        recipePreviewWorkflow = Model.Draft.Document.Id;
        recipePreviewCluster = Model.ClusterId;
        recipePreviewOpen = true;
    }

    /// <summary>子草稿を保存してから、選択範囲を一つの配置ノードへ置換します。</summary>
    /// <returns>レシピとタブの追加です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private Task ExtractSubworkflowAsync(IReadOnlyList<string> ids) => GuardAsync(async () =>
    {
        if (busy || Model.HasInvalidInputs)
        {
            return;
        }
        GraphExtractionPlan plan = new SubworkflowExtraction(Model.Draft.Document, ids.ToHashSet(StringComparer.Ordinal), catalog, Ui).Create();
        await Recipes.ExtractAsync(plan, lifetime.Token).ConfigureAwait(true);
        catalog.RemoveAll(item => string.Equals(item.Type, SubworkflowInterface.CallType(plan.Recipe.Id), StringComparison.Ordinal));
        catalog.Add(SubworkflowRecipeDescriptor.Create(plan.Recipe));
        message = Ui.Deferred("The SubWorkflow draft was created. New referenced recipes will be saved before the containing workflow.");
    });

    /// <summary>選択したノード定義を草稿へ追加します。</summary>
    private void AddNode(NodeDescriptor descriptor) => PlaceNode(descriptor, null);

    /// <summary>カタログからドラッグする定義と編集世代を固定します。</summary>
    private void BeginCatalogDrag(NodeDescriptor descriptor)
    {
        catalogDrag = descriptor;
        catalogDragGeneration = Model.Generation;
    }

    /// <summary>カタログのドラッグ状態を解除します。</summary>
    private void EndCatalogDrag() => catalogDrag = null;

    /// <summary>ドロップ位置を図の座標へ変換してノードを追加します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task DropNodeAsync(DragEventArgs args)
    {
        NodeDescriptor? descriptor = catalogDrag;
        catalogDrag = null;
        return descriptor is not null && catalogDragGeneration == Model.Generation ? AddAtAsync(new(descriptor, args.ClientX, args.ClientY))
                                                                                   : Task.CompletedTask;
    }

    /// <summary>指定位置へのノード追加を現在の編集実体で処理します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task AddAtAsync(NodePlacementRequest request) => GuardAsync(async () =>
    {
        if (graph is null || busy || Model.Locks.Whole(Model.Draft.Document.Id))
        {
            return;
        }

        int generation = Model.Generation;
        NodePosition? point = await graph.ClientToGraphAsync(request.ClientX, request.ClientY).ConfigureAwait(true);
        if (generation != Model.Generation)
        {
            return;
        }

        PlaceNode(request.Descriptor, new(Math.Max(0, point.X), Math.Max(0, point.Y)));
    });

    /// <summary>編集保護と世代を確認してノードを草稿へ配置します。</summary>
    private void PlaceNode(NodeDescriptor descriptor, NodePosition? position)
    {
        if (busy || Model.Locks.Whole(Model.Draft.Document.Id))
        {
            return;
        }

        JsonObject settings = [];
        if (descriptor.SettingsSchema.TryGetProperty(MatchProperties, out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                string legacyKey = "x-legacy";
                bool legacy = property.Value.TryGetProperty(legacyKey, out JsonElement ignored) && ignored.ValueKind == JsonValueKind.True;
                if (!legacy && property.Value.TryGetProperty(MatchDefault, out JsonElement value))
                {
                    settings[property.Name] = JsonNode.Parse(value.GetRawText());
                }
            }
        }

        string httpListener = "http.listen";
        if (string.Equals(descriptor.Type, httpListener, StringComparison.Ordinal))
        {
            settings["bodyFormat"] = "auto";
        }

        string? id = descriptor.Type.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N")[..5];
        NodeDefinition? node = new NodeDefinition(id, descriptor.Type, JsonSerializer.SerializeToElement(settings),
            Position: position ?? new(60 + (Model.Draft.Document.Nodes.Count % 3) * 390, 65 + (Model.Draft.Document.Nodes.Count / 3) * 420),
            StartOnLoad: descriptor.Kind == ENodeKind.Source, ContractVersion: descriptor.ContractVersion);
        Model.Change(document => document with { Nodes = [.. document.Nodes, node] });
        Model.SelectedNode = id;
        Model.SelectedWire = null;
    }

    /// <summary>現在の草稿を検証して問題箇所を表示します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ValidateAsync() => await GuardAsync(async () =>
    {
        if (invalid || Model.HasInvalidInputs)
        {
            throw new InvalidOperationException(Ui["Correct the invalid input values before continuing."]);
        }

        WorkflowDefinition checkedDocument = Model.Draft.Document;
        string? checkedCluster = Model.ClusterId;
        ValidationView? result = await Api.PostAsync<ValidationView>("/api/workflows/validate", checkedDocument, lifetime.Token).ConfigureAwait(true);
        if (result is not null && string.Equals(checkedCluster, Model.ClusterId, StringComparison.Ordinal))
        {
            Diagnostics.SetValidation(checkedCluster, checkedDocument, Model.Draft.Document, result.Issues);
        }
        message = result?.Valid == true ? Ui.Deferred("Workflow structure and settings are valid. Runtime resources are checked during execution.")
                                        : () => string.Join("\n", result?.Issues.Select(issue => Ui[issue.Message]) ?? []);
    }).ConfigureAwait(true);

    /// <summary>草稿に残る競合候補の比較画面を開きます。</summary>
    private void OpenConflicts()
    {
        collaborationOpen = false;
        conflicts = Model.Draft.Conflicts;
        saveConflict = false;
        conflictOpen = true;
    }

    /// <summary>確認した競合または実体の解消方法を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ResolveAsync(IReadOnlyDictionary<string, int> choices)
    {
        await GuardAsync(async () =>
        {
            conflictOpen = false;
            if (saveConflict)
            {
                await SaveAsync(choices.ToDictionary(pair => pair.Key, pair => pair.Value == -1 ? EEditResolution.KeepCurrent
                                                                                                : EEditResolution.UseEdited)).ConfigureAwait(true);
            }
            else
            {
                Collaboration.Resolve(conflicts, choices);
                await Collaboration.SynchronizeAsync(true, Connection.Current?.PeerId ?? "browser", lifetime.Token).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>現在の草稿をJSONとしてブラウザーから出力します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ExportAsync() => await GuardAsync(async () => await Storage.DownloadAsync(Model.Draft.Document.Id + ".json", Model.Draft.Document, lifetime.Token).ConfigureAwait(true)).ConfigureAwait(true);

    /// <summary>選択ファイルを有限のサイズで読み込んで取込入力へ設定します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadImportAsync(InputFileChangeEventArgs e)
    {
        using StreamReader? reader = new StreamReader(e.File.OpenReadStream(16777216, cancellationToken: lifetime.Token));
        importText = await reader.ReadToEndAsync(lifetime.Token).ConfigureAwait(true);
    }

    /// <summary>入力したJSONまたはYAMLを検証して草稿へ取り込みます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ImportAsync() => await GuardAsync(async () =>
    {
        WorkflowDefinition? definition = importText.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<WorkflowDefinition>(importText, ContractJson.Options)
                                                                                : await Api.PostAsync<WorkflowDefinition>("/api/workflows/parse", new WorkflowYamlImport(importText), lifetime.Token).ConfigureAwait(true);
        Model.Import(definition ?? throw new InvalidOperationException(Ui["The workflow could not be parsed."]));
        importOpen = false;
    }).ConfigureAwait(true);
}
