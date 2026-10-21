using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>WFDashboardに表示したStateだけを、一つの変更配信接続で観測します。</summary>
// 抑制理由: Razorが生成する公開partialと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class WfDashboardStateFeed
{
    [Inject]
    private IJSRuntime JS
    {
        get; set;
    } = null!;

    /// <summary>現在配置しているカードです。</summary>
    // 抑制理由: BlazorのParameterとして親が配置変更後の不変一覧を渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "親コンポーネントが更新するBlazorのParameterです。")]
    [Parameter]
    public IReadOnlyList<NodeDefinition> Nodes
    {
        get; set;
    } = [];

    /// <summary>参照先クラスタです。</summary>
    [Parameter]
    public string? Cluster
    {
        get; set;
    }

    /// <summary>参照先Peerです。</summary>
    [Parameter]
    public string? Peer
    {
        get; set;
    }

    /// <summary>再起動した実体へ古い応答を適用しない識別子です。</summary>
    [Parameter]
    public Guid? Instance
    {
        get; set;
    }

    /// <summary>取得結果を描画するカード一覧です。</summary>
    [Parameter, EditorRequired]
    public RenderFragment<WfDashboardStateFeed> ChildContent
    {
        get; set;
    } = null!;

    private readonly Dictionary<string, StateStreamView> values = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim changes = new(1, 1);
    private IJSObjectReference? module, subscription;
    private DotNetObjectReference<WfDashboardStateFeed>? callback;
    private string? signature;
    private int generation;
    private bool disposed;

    /// <summary>値の不存在と通信切断を区別します。</summary>
    public string? Problem
    {
        get; private set;
    }

    /// <summary>受信済みの値だけを返します。</summary>
    /// <returns>最後の表示値、または初期待ちのnullです。</returns>
    public StateStreamView? Find(string id) => values.GetValueOrDefault(id);

    /// <summary>配置や接続先が変わった場合だけ購読を張り替えます。</summary>
    /// <returns>初回読取りを伴う接続の開始です。</returns>
    // 抑制理由: Blazorの固定署名で画面寿命のトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed)
        {
            return;
        }
        try
        {
            await changes.WaitAsync(UiCancellation).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (disposed)
        {
            return;
        }
        try
        {
            await UpdateAsync(UiCancellation).ConfigureAwait(true);
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>描画再入時も古い接続を解放してから新しい接続を所有します。</summary>
    /// <returns>最新の購読指定の反映です。</returns>
    private async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        StateStreamTarget[] targets = Nodes.OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => (node.Id, Reference: WfDashboardStateReference.Read(node))).Where(item => item.Reference is not null)
            .Select(item => new StateStreamTarget(item.Id, item.Reference!.Target)).ToArray();
        string requested = JsonSerializer.Serialize(new { Cluster, Peer, Instance, targets }, ContractJson.Options);
        if (disposed || string.Equals(signature, requested, StringComparison.Ordinal))
        {
            return;
        }
        signature = requested;
        int version = ++generation;
        values.Clear();
        Problem = null;
        await CloseAsync().ConfigureAwait(true);
        if (targets.Length == 0)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Cluster) || string.IsNullOrWhiteSpace(Peer) || Instance is null)
        {
            Problem = "State observation disconnected";
            StateHasChanged();
            return;
        }
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./wf-dashboard-state.js").ConfigureAwait(true);
            callback ??= DotNetObjectReference.Create(this);
            subscription = await module.InvokeAsync<IJSObjectReference>("watch", cancellationToken, callback, version, Cluster, Peer, Instance,
                JsonSerializer.SerializeToElement(targets, ContractJson.Options)).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JSException or OperationCanceledException)
        {
            if (!disposed)
            {
                Problem = "State observation disconnected";
                StateHasChanged();
            }
        }
    }

    /// <summary>旧接続から遅れて届いた応答を捨て、変更カードだけを更新します。</summary>
    /// <returns>受信値の画面反映です。</returns>
    // 抑制理由: JSからの固定署名で、generationとdisposedにより画面寿命を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "JSInvokableの固定署名です。")]
    [JSInvokable]
    public Task ReceiveAsync(int version, StateStreamView[]? updates, string? problem)
    {
        if (disposed || version != generation)
        {
            return Task.CompletedTask;
        }
        if (updates is not null)
        {
            foreach (StateStreamView view in updates)
            {
                values[view.Id] = view;
            }
        }
        Problem = problem;
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>再試行タイマーとWebSocketを先に終了します。</summary>
    /// <returns>購読の解放です。</returns>
    // 抑制理由: 終了境界では取消済みの画面トークンを使わず、JS資源を解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "所有するJS資源の終了境界です。")]
    private async Task CloseAsync()
    {
        if (subscription is { } current)
        {
            subscription = null;
            try
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = current.ConfigureAwait(true);
                await current.InvokeVoidAsync("dispose").ConfigureAwait(true);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    /// <summary>画面離脱後に変更配信とDotNet参照を保持しません。</summary>
    /// <returns>所有資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        generation++;
        await base.DisposeAsync().ConfigureAwait(true);
        await changes.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await CloseAsync().ConfigureAwait(true);
            callback?.Dispose();
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            changes.Release();
            changes.Dispose();
        }
    }
}
