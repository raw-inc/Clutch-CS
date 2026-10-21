using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>最新の文字列と、利用者が読み込んだ媒体を区別して表示します。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class NodePreviewPanel
{
    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    [Inject]
    private EditorPreviews Previews
    {
        get; set;
    } = null!;

    [Inject]
    private IJSRuntime Script
    {
        get; set;
    } = null!;

    /// <summary>表示対象となる終端ノードです。</summary>
    [Parameter, EditorRequired]
    public NodeDefinition Node
    {
        get; set;
    } = null!;

    /// <summary>配置の実行値を見る場合の元WFです。</summary>
    [Parameter]
    public string? WorkflowId
    {
        get; set;
    }

    /// <summary>配置の実行元クラスタです。</summary>
    [Parameter]
    public string? ClusterId
    {
        get; set;
    }

    /// <summary>読み取り専用画面では編集モデルの静的値を流用しません。</summary>
    [Parameter]
    public bool RuntimeOnly
    {
        get; set;
    }

    /// <summary>WFDashboardでは本文を中心に表示し、版情報を省きます。</summary>
    [Parameter]
    public bool Compact
    {
        get; set;
    }

    /// <summary>配置を観測して取得したスナップショットです。</summary>
    // 抑制理由: 描画ごとに不変の観測一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "描画ごとに不変の観測一覧を受け取ります。")]
    public IReadOnlyList<NodePreviewSnapshot>? Snapshots
    {
        get; set;
    }

    private string? PreviewCluster => ClusterId ?? Model.ClusterId;
    private string PreviewWorkflow => WorkflowId ?? Model.Draft.Document.Id;

    /// <summary>取得済みテキストを進捗値として読み、破損値を前回値で隠しません。</summary>
    /// <returns>今回の進捗または未取得です。</returns>
    private Contracts.Values.ProgressValue? ReadProgress()
    {
        try
        {
            return textRead.Text is null ? null
                                         : JsonSerializer.Deserialize<Contracts.Values.ProgressValue>(textRead.Text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly List<NodePreviewSnapshot> options = [];
    private NodePreviewSnapshot? current, loaded;
    private bool HasScopedProgress => Node.Type is "control.progress" && options.Any(item => item.Session is not null);

    /// <summary>同じ実行・要素を複数Peerで観測しても一つにまとめ、別実行は分離します。</summary>
    /// <returns>実行の開始順で並べた最新観測です。</returns>
    private NodePreviewSnapshot[] ProgressSessions() => options.Where(item => item.Session is not null)
        .GroupBy(item => (item.Session!.RunId, item.Session.OwnerInstanceId, item.Session.ScopeId))
        .Select(group => group.MaxBy(item => item.CapturedAt)!).OrderBy(item => item.Session!.StartedAt)
        .ThenBy(item => item.Session!.RunId).ThenBy(item => item.Session!.ScopeId).ToArray();
    private string selectedPeer = string.Empty;
    private string? problem, identity;
    private readonly PreviewTextRead textRead = new();
    private static string PlainText => "text/plain";
    private NodePreviewSnapshot? DisplaySnapshot => string.Equals(current?.MediaType, PlainText, StringComparison.Ordinal) && current?.Problem is null ? textRead.Snapshot ?? current
                                                                                                                                                       : current;
    private bool busy, clearRequested, historyVisible;
    private int generation;
    private ElementReference mediaRoot;
    private IJSObjectReference? module, media;
    private readonly SemaphoreSlim mediaGate = new(1, 1);

    /// <summary>表示対象の変更と、新しい実行値の到着を別々に扱います。</summary>
    /// <returns>小さいテキストだけの自動取得です。</returns>
    // 抑制理由: Blazorの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。画面寿命のトークンを使用します。")]
    protected override async Task OnParametersSetAsync()
    {
        string key = PreviewCluster + "/" + PreviewWorkflow + "/" + Node.Id;
        if (!string.Equals(identity, key, StringComparison.Ordinal))
        {
            identity = key;
            selectedPeer = string.Empty;
            historyVisible = false;
            Reset();
        }
        options.Clear();
        options.AddRange(Snapshots ?? Previews.Find(PreviewCluster, PreviewWorkflow, Node.Id));
        options.RemoveAll(item => !Previews.IsCurrent(item));
        current = options.FirstOrDefault(item => selectedPeer.Length == 0 || string.Equals(item.PeerId, selectedPeer, StringComparison.Ordinal));
        if (HasScopedProgress)
        {
            // 新形式のProgressは一覧内の小さい数値だけで表示します。旧Peerの本文取得は従来経路で維持します。
            current = options.FirstOrDefault(item => item.Session is null);
        }
        if (historyVisible)
        {
            return;
        }
        if (current is null || current.Problem is not null)
        {
            Reset();
            return;
        }
        string plain = "text/plain";
        if (!string.Equals(current.MediaType, plain, StringComparison.Ordinal))
        {
            return;
        }
        if (loaded is not null)
        {
            loaded = null;
            clearRequested = true;
        }
        NodePreviewSnapshot snapshot = current;
        problem = null;
        string cluster = PreviewCluster!;
        await textRead.UpdateAsync(snapshot, token => Previews.ReadAsync(cluster, snapshot, token), UiCancellation).ConfigureAwait(true);
    }

    /// <summary>表示方法だけを切り替え、媒体を停止して保存済みWFや実行には触れません。</summary>
    /// <returns>選択した表示の読取りです。</returns>
    // 抑制理由: UIイベントの固定署名です。画面寿命のトークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。画面寿命のトークンを使用します。")]
    internal async Task SetHistoryAsync(bool visible)
    {
        historyVisible = visible;
        Reset();
        await OnParametersSetAsync().ConfigureAwait(true);
    }

    /// <summary>Peerを変更したときは前の媒体を停止します。</summary>
    /// <returns>新しい選択の表示です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task SelectPeerAsync(ChangeEventArgs args)
    {
        selectedPeer = args.Value?.ToString() ?? string.Empty;
        Reset();
        await OnParametersSetAsync().ConfigureAwait(true);
    }

    /// <summary>媒体を明示操作で読み、URLや任意HTMLとして入力を評価しません。</summary>
    /// <returns>ブラウザーの媒体要素への所有権の引継ぎです。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task LoadMediaAsync()
    {
        if (current is null || busy || PreviewCluster is null)
        {
            return;
        }
        NodePreviewSnapshot snapshot = current;
        string cluster = PreviewCluster;
        int version = generation;
        await mediaGate.WaitAsync(UiCancellation).ConfigureAwait(true);
        busy = true;
        problem = null;
        try
        {
            byte[] data = await Previews.ReadAsync(cluster, snapshot, UiCancellation).ConfigureAwait(true);
            if (version != generation || ComponentDisposed)
            {
                return;
            }
            module ??= await Script.InvokeAsync<IJSObjectReference>("import", UiCancellation, "./node-preview.js").ConfigureAwait(true);
            media ??= await module.InvokeAsync<IJSObjectReference>("attachPreview", UiCancellation, mediaRoot).ConfigureAwait(true);
            using MemoryStream stream = new(data, false);
            using DotNetStreamReference reference = new(stream);
            string? error = await media.InvokeAsync<string?>("load", UiCancellation, reference, snapshot.MediaType,
                Ui["preview.browserUnsupported"], Ui["preview.mediaBudget"]).ConfigureAwait(true);
            if (version == generation && !ComponentDisposed)
            {
                problem = error;
                loaded = error is null ? snapshot
                                       : null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException or JSException)
        {
            if (version == generation && !ComponentDisposed)
            {
                problem = "preview.unavailable";
            }
        }
        finally
        {
            busy = false;
            mediaGate.Release();
        }
    }

    /// <summary>手動で媒体の保持を解放します。</summary>
    /// <returns>要素とBlob URLの解放です。</returns>
    // 抑制理由: UIイベントの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIイベントの固定署名です。")]
    private async Task ClearMediaAsync()
    {
        await mediaGate.WaitAsync(UiCancellation).ConfigureAwait(true);
        try
        {
            loaded = null;
            if (media is not null)
            {
                await media.InvokeVoidAsync("clear", UiCancellation).ConfigureAwait(true);
            }
        }
        finally
        {
            mediaGate.Release();
        }
    }

    /// <summary>描画先が更新された後で古い媒体を解放します。</summary>
    /// <returns>媒体解放の完了です。</returns>
    // 抑制理由: Blazorの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (clearRequested)
        {
            clearRequested = false;
            await ClearMediaAsync().ConfigureAwait(true);
        }
    }

    /// <summary>異なる選択の遅延応答を表示しません。</summary>
    private void Reset()
    {
        generation++;
        textRead.Clear();
        problem = null;
        loaded = null;
        clearRequested = true;
    }

    /// <summary>再生とBlob URLをノードの表示寿命で解放します。</summary>
    /// <returns>ブラウザー資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        generation++;
        textRead.Clear();
        await mediaGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (media is not null)
            {
                await media.InvokeVoidAsync("dispose").ConfigureAwait(true);
                await media.DisposeAsync().ConfigureAwait(true);
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
            mediaGate.Release();
            mediaGate.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
