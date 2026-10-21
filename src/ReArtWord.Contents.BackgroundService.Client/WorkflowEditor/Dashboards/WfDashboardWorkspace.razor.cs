using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>同じクラスタ内のノードを、配線を持たないカード画面へ集約します。</summary>
// 抑制理由: Razorが生成する公開partialと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class WfDashboardWorkspace
{
    /// <summary>WFDashboard自身の配置を既存保存処理へ渡します。</summary>
    [Parameter]
    public EventCallback Save
    {
        get; set;
    }

    /// <summary>既存の文書選択を開きます。</summary>
    [Parameter]
    public EventCallback Open
    {
        get; set;
    }

    /// <summary>取込・出力・削除など文書共通の操作です。</summary>
    [Parameter]
    public EventCallback Tools
    {
        get; set;
    }

    /// <summary>親画面の保存中です。</summary>
    [Parameter]
    public bool Busy
    {
        get; set;
    }

    private WfDashboardSession session = null!;
    private WfDashboardActions actions = null!;
    private Task polling = Task.CompletedTask;
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private bool catalogOpen, refreshing;
    private string? dragged, dropTarget;
    private ElementReference board;
    private bool CanOperate => Connection.Connected && session.Available && !Busy
        && string.Equals(Model.ClusterId, Api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(Model.PeerId, Api.SelectedPeerId, StringComparison.Ordinal);
    private bool LayoutLocked => Busy || Model.Locks.Whole(Model.Draft.Document.Id);
    private NodeDefinition[] Cards => Model.Draft.Document.Nodes.OrderBy(node => node.Position?.Y ?? 0).ThenBy(node => node.Position?.X ?? 0).ToArray();

    /// <summary>画面ごとの有限な状態を所有し、文書変更時は別コンポーネントへ切り替えます。</summary>
    /// <returns>初回の取得です。</returns>
    // 抑制理由: Blazorの固定署名でUiCancellationを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを渡します。"),
        System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "pollingを所有し、DisposeAsyncで取消しと完了を確認します。")]
    protected override async Task OnInitializedAsync()
    {
        session = new(Api);
        actions = new(Api, Journal);
        catalogOpen = Model.Draft.Document.Nodes.Count == 0;
        await RefreshAsync(UiCancellation).ConfigureAwait(true);
        polling = PollAsync(UiCancellation);
    }

    /// <summary>表示中だけ観測し、重なる要求を増やしません。</summary>
    /// <returns>画面終了までの所有タスクです。</returns>
    private async Task PollAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>現在の参照だけを取得し、編集中のValue入力には触れません。</summary>
    /// <returns>小さいメタデータとプレビューの確認です。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (refreshing || !Connection.Connected || Model.ClusterId is not { } cluster || Model.PeerId is not { } peer)
        {
            return;
        }
        refreshing = true;
        try
        {
            WfDashboardReference[] references = Cards.Select(Reference).OfType<WfDashboardReference>().ToArray();
            await session.RefreshAsync(cluster, peer, references, Connection.Current?.Peers ?? [], cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>元ノードは複製せず、新しい参照カードだけを配置します。</summary>
    private void AddCard(WfDashboardCatalogItem item) => AddCard(WfDashboardReference.CardType, item.Reference.ToSettings());

    /// <summary>参照と表示専用カードを、同じ配置・Undo・保存の単位で追加します。</summary>
    private void AddCard(string type, JsonElement settings)
    {
        if (LayoutLocked || 128 <= Model.Draft.Document.Nodes.Count)
        {
            return;
        }
        double position = Cards.Select(node => node.Position?.X ?? 0).DefaultIfEmpty(-1).Max() + 1;
        NodeDefinition card = new("card-" + Guid.NewGuid().ToString("N")[..8], type, settings, Position: new(position, 0));
        Model.Change(document => document with { Nodes = [.. document.Nodes, card] });
    }

    /// <summary>参照だけを取り除き、元WFや元ノードを削除しません。</summary>
    private void RemoveCard(string id)
    {
        if (!LayoutLocked)
        {
            Model.Change(document => document with { Nodes = document.Nodes.Where(node => !string.Equals(node.Id, id, StringComparison.Ordinal)).ToArray() });
        }
    }

    /// <summary>カード間や末尾の空白も含め、離した位置へカードを挿入します。</summary>
    /// <returns>表示位置の取得と草稿への配置反映です。</returns>
    // 抑制理由: Razorイベントの固定署名で、JS呼出しには画面寿命の取消信号を渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Razorイベントの固定署名でUiCancellationを渡します。")]
    private async Task DropAsync(DragEventArgs args)
    {
        string? id = dragged;
        dragged = dropTarget = null;
        if (LayoutLocked || id is null)
        {
            return;
        }
        try
        {
            string? before = await JS.InvokeAsync<string?>("contentsBrowser.dashboardDropTarget", UiCancellation, board, args.ClientX, args.ClientY).ConfigureAwait(true);
            if (!ComponentDisposed && !LayoutLocked)
            {
                NodeDefinition[] cards = Cards;
                NodeDefinition[] reordered = ReorderCards(cards, id, before);
                if (!ReferenceEquals(cards, reordered))
                {
                    Model.Change(document => document with { Nodes = reordered });
                }
            }
        }
        catch (OperationCanceledException) when (UiCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>参照情報を保ったまま表示順を配置へ記録し、同位置や不明なカードは変更しません。</summary>
    /// <returns>移動後の配置、または変更がない元の配列です。</returns>
    internal static NodeDefinition[] ReorderCards(NodeDefinition[] cards, string id, string? before)
    {
        int previous = Array.FindIndex(cards, card => string.Equals(card.Id, id, StringComparison.Ordinal));
        if (previous < 0 || string.Equals(id, before, StringComparison.Ordinal))
        {
            return cards;
        }
        List<NodeDefinition> reordered = [.. cards];
        NodeDefinition moving = reordered[previous];
        reordered.RemoveAt(previous);
        int index = reordered.Count;
        if (before is not null)
        {
            index = reordered.FindIndex(card => string.Equals(card.Id, before, StringComparison.Ordinal));
        }
        if (index < 0 || index == previous)
        {
            return cards;
        }
        reordered.Insert(index, moving);
        return reordered.Select((node, order) => node with { Position = new(order, 0) }).ToArray();
    }

    /// <summary>描画のために不正な参照を実行・変換しません。</summary>
    /// <returns>読み取れた物理参照だけです。</returns>
    private static WfDashboardReference? Reference(NodeDefinition card)
    {
        try
        {
            return WfDashboardReference.Read(card);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>明示したボタン押下を元WFへ一回渡します。</summary>
    /// <returns>受付結果の表示です。</returns>
    // 抑制理由: RazorイベントでUiCancellationを下位I/Oへ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "RazorイベントでUiCancellationを下位I/Oへ渡します。")]
    private async Task PressAsync(NodeDefinition card)
    {
        bool accepted = false;
        await OperateAsync(card, async state =>
        {
            WorkflowRunStatus result = await actions.PressAsync(Model.ClusterId!, Model.PeerId!, state, UiCancellation).ConfigureAwait(true);
            accepted = result.Running || result.Outcome == EOutcome.Succeeded;
            string message = result.Reason ?? result.Outcome.ToString();
            if (result.Running)
            {
                message = "The source workflow accepted the button press.";
            }
            Notify(message);
        }).ConfigureAwait(true);
        if (accepted && !ComponentDisposed)
        {
            try
            {
                await JS.InvokeVoidAsync("contentsBrowser.dashboardActionFeedback", UiCancellation, board, card.Id).ConfigureAwait(true);
            }
            catch (JSException)
            {
                // 装飾の描画失敗で、受付済みの操作結果を失敗へ置き換えません。
            }
            catch (OperationCanceledException) when (UiCancellation.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Valueの型固定と比較版を確認した要求だけを元WFへ保存します。</summary>
    /// <returns>保存・共有・反映の確認です。</returns>
    // 抑制理由: RazorイベントでUiCancellationを下位I/Oへ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "RazorイベントでUiCancellationを下位I/Oへ渡します。")]
    private Task SaveValueAsync(NodeDefinition card, WfDashboardValueInput input) => OperateAsync(card, async state =>
    {
        WorkflowSaveResult result = await actions.SaveValueAsync(Model.ClusterId!, Model.PeerId!, state, input.Revision, input.Text, UiCancellation).ConfigureAwait(true);
        if (result.Saved.Outcome != EOutcome.Succeeded || result.Shared.Outcome != EOutcome.Succeeded)
        {
            string message = result.Saved.Reason ?? "The Value save could not be confirmed. Check operation history.";
            if (result.Validation.Count != 0)
            {
                message = result.Validation[0].Message;
            }
            Notify(message);
            return;
        }
        string resultMessage = "Value saved. Activation is pending in the source workflow.";
        if (result.Activation.Outcome == EOutcome.Succeeded)
        {
            resultMessage = "Value saved and applied to the source workflow.";
        }
        Notify(resultMessage);
    });

    /// <summary>同じカードの重複送信を抑え、確認不能では自動再送しません。</summary>
    /// <returns>一回の操作と確認状態の更新です。</returns>
    // 抑制理由: 画面寿命のUiCancellationを操作と再取得に共有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面寿命のUiCancellationを操作と再取得に共有します。")]
    private async Task OperateAsync(NodeDefinition card, Func<WfDashboardNodeState, Task> operation)
    {
        WfDashboardReference? reference = Reference(card);
        WfDashboardNodeState? state = null;
        if (reference is not null)
        {
            state = session.Find(reference);
        }
        if (!CanOperate || state is null || !pending.Add(card.Id))
        {
            return;
        }
        try
        {
            await operation(state).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            Notify(exception.Message);
        }
        finally
        {
            pending.Remove(card.Id);
            session.Invalidate();
        }
        if (!ComponentDisposed)
        {
            await RefreshAsync(UiCancellation).ConfigureAwait(true);
        }
    }

    /// <summary>既存の通知カードと同じ場所へ結果を表示します。</summary>
    private void Notify(string text)
    {
        if (!ComponentDisposed)
        {
            Notices.Set("operation", () => Ui[text]);
        }
    }

    /// <summary>タブ移動で取得と描画を終了します。</summary>
    /// <returns>画面が所有した周期処理の終了です。</returns>
    // 抑制理由: 所有する画面処理の終了境界です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "所有する画面処理の終了境界です。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await polling.ConfigureAwait(true);
    }
}
