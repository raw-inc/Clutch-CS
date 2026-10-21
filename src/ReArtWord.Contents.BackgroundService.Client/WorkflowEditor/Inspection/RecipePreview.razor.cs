using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;

/// <summary>元の草稿を変更せず、実行計画の一配置だけを読み取り専用で観測します。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class RecipePreview
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>実行される元WFのクラスタです。</summary>
    [Parameter, EditorRequired]
    public string ClusterId
    {
        get; set;
    } = string.Empty;

    /// <summary>内部定義ではなく、配置を含む元WFの識別子です。</summary>
    [Parameter, EditorRequired]
    public string WorkflowId
    {
        get; set;
    } = string.Empty;

    /// <summary>最初に開く配置ノードです。</summary>
    [Parameter, EditorRequired]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>現在参加している実行Peerを観測します。</summary>
    // 抑制理由: Blazorから新しい一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorから新しい一覧を受け取ります。")]
    public IReadOnlyList<PeerInfo> Peers
    {
        get; set;
    } = [];

    private readonly Stack<string> history = [];
    private string? identity, selected, problem;
    private WorkflowRecipePreview? view;
    private GraphTypeInference? inference;
    private EditorDraft? observed;
    private EditorNodeActivity activity = null!;
    private EditorPreviews previews = null!;
    private Task? polling;
    private bool loading;
    private double zoom = 1;
    private int generation;
    private DateTimeOffset nextLayout;

    /// <summary>編集画面と観測スコープを共有しない短命な読み取り状態を作ります。</summary>
    protected override void OnInitialized()
    {
        activity = new(Api);
        previews = new(Api);
    }

    /// <summary>配置の切替時だけ最初の内部レイアウトを取得します。</summary>
    /// <returns>初期表示の完了です。</returns>
    // 抑制理由: Blazorの固定署名です。監視TaskはDisposeAsyncで終了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "監視Taskはpollingが所有し、DisposeAsyncで終了を確認します。")]
    protected override async Task OnParametersSetAsync()
    {
        string key = ClusterId + "/" + WorkflowId + "/" + NodeId;
        if (!string.Equals(key, identity, StringComparison.Ordinal))
        {
            identity = key;
            history.Clear();
            selected = NodeId;
            view = null;
            generation++;
            await LoadAsync(UiCancellation).ConfigureAwait(true);
        }
        polling ??= PollAsync(UiCancellation);
    }

    /// <summary>同じ配置の変更分だけを取得し、設定とポートを同じ版へ揃えます。</summary>
    /// <returns>現在の実効レイアウトの確認です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        int version = generation;
        loading = true;
        try
        {
            if (!string.Equals(ClusterId, Api.SelectedClusterId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The connection changed.");
            }
            WorkflowRecipePreview? result = await Api.PostAsync<WorkflowRecipePreview>("/api/workflows/recipe-preview", new WorkflowRecipePreviewQuery(WorkflowId, selected!, view?.Hash), cancellationToken).ConfigureAwait(true);
            if (version == generation && result is not null)
            {
                view = result;
                inference = new(result.Instance.Layout, result.Catalog ?? []);
                WorkflowDefinition document = result.Instance.Layout with { IsRecipe = false, Nodes = result.Instance.Layout.Nodes.Where(node => !SubworkflowInterface.IsBoundary(node.Type)).ToArray() };
                observed = new(document, document, result.Revision, document, null, result.Revision, false, []);
                if (!result.IsRuntime)
                {
                    activity.Reset();
                    previews = new(Api);
                }
            }
            problem = null;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (!ComponentDisposed && version == generation)
            {
                problem = "This instance is not available. Save the containing workflow and its referenced recipes first.";
                activity.Reset();
            }
        }
        finally
        {
            loading = false;
            nextLayout = Api.UtcNow.AddSeconds(3);
        }
    }

    /// <summary>値を生成せず、処理中状態と明示Previewだけを定期照会します。</summary>
    /// <returns>画面が閉じるまでの観測です。</returns>
    private async Task PollAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (loading)
                {
                    continue;
                }
                if (nextLayout <= Api.UtcNow)
                {
                    await LoadAsync(cancellationToken).ConfigureAwait(true);
                }
                if (observed is not null && view?.IsRuntime == true && problem is null)
                {
                    await activity.RefreshAsync(ClusterId, observed, Peers, true, cancellationToken).ConfigureAwait(true);
                    await previews.RefreshAsync(ClusterId, observed.Document, Peers, cancellationToken).ConfigureAwait(true);
                }
                if (!ComponentDisposed)
                {
                    StateHasChanged();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 画面の破棄で観測を終了します。ノードの実行や停止は要求しません。
        }
    }

    /// <summary>同じ元WFに属する内側の配置を開きます。</summary>
    /// <returns>内部表示の切替です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task OpenNestedAsync(string id)
    {
        if (loading)
        {
            return;
        }
        history.Push(selected!);
        selected = id;
        view = null;
        observed = null;
        generation++;
        await LoadAsync(UiCancellation).ConfigureAwait(true);
    }

    /// <summary>一つ外側の配置へ戻ります。</summary>
    /// <returns>表示の切替です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task BackAsync()
    {
        if (history.TryPop(out string? id))
        {
            selected = id;
            view = null;
            observed = null;
            generation++;
            await LoadAsync(UiCancellation).ConfigureAwait(true);
        }
    }

    private double Width => Math.Max(1200, view?.Instance.Layout.Nodes.Select(node => (node.Position?.X ?? 60) + Size(node).Width + 60).DefaultIfEmpty().Max() ?? 0);
    private double Height => Math.Max(700, view?.Instance.Layout.Nodes.Select(node => (node.Position?.Y ?? 80) + Size(node).Height + 60).DefaultIfEmpty().Max() ?? 0);

    /// <summary>公開したポートと実際の旧配線端点を表示します。</summary>
    /// <returns>描画順の端点です。</returns>
    private List<string> Ports(NodeDefinition node, bool outbound)
    {
        NodeDescriptor? descriptor = inference?.EditorDescriptor(node.Id);
        IEnumerable<PortDescriptor> ports = outbound ? descriptor?.Outputs ?? []
                                                     : descriptor?.Inputs ?? [];
        IEnumerable<string> connected = view!.Instance.Layout.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.ToPort);
        if (outbound)
        {
            connected = view.Instance.Layout.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.FromPort);
        }
        return ports.Where(port => !port.IsLegacy).Select(port => port.Name).Concat(connected).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>利用者が広げたPreviewを同じ寸法で表示します。</summary>
    /// <returns>読み取り表示の寸法です。</returns>
    private NodeSize Size(NodeDefinition node)
    {
        int count = Math.Max(Ports(node, false).Count, Ports(node, true).Count);
        string[] flexible = ["control.preview", "control.progress", "value.code"];
        if (flexible.Contains(node.Type, StringComparer.Ordinal))
        {
            return new(Math.Clamp(node.Size?.Width ?? 560, 360, 1600), Math.Max(150 + count * 26, Math.Clamp(node.Size?.Height ?? 520, 400, 2000)));
        }
        return new(320, 180 + count * 26 + Math.Min(400, node.Settings.EnumerateObject().Count() * 45));
    }

    /// <summary>カードと配線に同じ座標を使います。</summary>
    /// <returns>未定義端点ならnullです。</returns>
    private NodePosition? Endpoint(string id, string port, bool outbound)
    {
        NodeDefinition? node = view!.Instance.Layout.Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));
        if (node is null)
        {
            return null;
        }
        NodePosition point = node.Position ?? new(60, 80);
        return new(point.X + (outbound ? Size(node).Width
                                       : 0), point.Y + 68 + Math.Max(0, Ports(node, outbound).IndexOf(port)) * 26);
    }

    /// <summary>配置接頭辞の代わりに定義内の識別子を表示します。</summary>
    /// <returns>読みやすい元のIDです。</returns>
    private static string LocalId(string id) => Uri.UnescapeDataString(id[(id.LastIndexOf('/') + 1)..]);

    /// <summary>長い公開名は全体レイアウトを崩さず表示します。</summary>
    /// <returns>ポートの短い表示名です。</returns>
    private static string Short(string value) => value.Length <= 20 ? value
                                                                    : value[..19] + "…";

    /// <summary>閉じた画面の照会を取り消し、所有Taskの終了を確認します。</summary>
    /// <returns>観測資源の解放です。</returns>
    // 抑制理由: Blazorの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        if (polling is not null)
        {
            await polling.ConfigureAwait(true);
        }
        GC.SuppressFinalize(this);
    }
}
