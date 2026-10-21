using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>選択したWFの一回分だけを予約し、画面を閉じても取得予約を再実行しません。</summary>
// 抑制理由: Razorが生成する公開partialと同じ可視性を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial型です。")]
public sealed partial class WorkflowDebugPanel
{
    /// <summary>保存済みの診断対象です。</summary>
    [Parameter, EditorRequired]
    public string WorkflowId
    {
        get; set;
    } = string.Empty;

    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private IJSRuntime Script
    {
        get; set;
    } = null!;

    private WorkflowDebugView? state;
    private string? problem;
    private string? peer;
    private string? cluster;
    private bool busy;
    private int offset;
    private readonly Stack<int> previous = new();
    private Task polling = Task.CompletedTask;
    private bool Active => state?.State is EWorkflowDebugState.Preparing or EWorkflowDebugState.Armed or EWorkflowDebugState.Recording;

    /// <summary>接続先を固定し、予約を作らず既存の状態だけを取得します。</summary>
    /// <returns>初期表示と、所有する定期参照の開始です。</returns>
    // 抑制理由: 定期参照はフィールドで保持し、DisposeAsyncで取消後の終了をawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "定期参照のTaskを所有し、DisposeAsyncで終了まで待ちます。")]
    protected override async Task OnInitializedAsync()
    {
        peer = Api.SelectedPeerId;
        cluster = Api.SelectedClusterId;
        await RefreshAsync(UiCancellation).ConfigureAwait(true);
        polling = PollAsync(UiCancellation);
    }

    /// <summary>ボタン押下時にだけ一回分の操作IDを作り、応答不明時は照会へ戻します。</summary>
    /// <returns>予約の確認結果です。</returns>
    private async Task ArmAsync(CancellationToken cancellationToken = default)
    {
        busy = true;
        try
        {
            state = await Api.PostForPeerAsync<WorkflowDebugView>("/api/workflow-debug/arm", new WorkflowDebugRequest(Guid.NewGuid(), WorkflowId), peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (state is not null)
            {
                state = await ReadAsync(state.Arm.Id, 0, cancellationToken).ConfigureAwait(true);
            }
            offset = 0;
            previous.Clear();
            problem = null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            problem = "Debug operation is unconfirmed. Refresh the existing capture before trying another reservation.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>通常のWF実行には触れず、診断だけを停止します。</summary>
    /// <returns>元記録の停止結果です。</returns>
    private async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (state is null)
        {
            return;
        }
        busy = true;
        try
        {
            state = await Api.PostForPeerAsync<WorkflowDebugView>("/api/workflow-debug/" + state.Arm.Id.ToString("D") + "/cancel", new { }, peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (state is not null)
            {
                state = await ReadAsync(state.Arm.Id, 0, cancellationToken).ConfigureAwait(true);
            }
            offset = 0;
            previous.Clear();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            problem = "Debug operation is unconfirmed. Refresh the existing capture before trying another reservation.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>現在のページを参照し、完了記録を別の入力で上書きしません。</summary>
    /// <returns>取得した状態です。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        try
        {
            if (state is null)
            {
                state = await Api.GetForPeerAsync<WorkflowDebugView>("/api/workflows/" + Uri.EscapeDataString(WorkflowId) + "/debug", peer, cluster, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                state = await ReadAsync(state.Arm.Id, offset, cancellationToken).ConfigureAwait(true);
            }
            problem = null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            problem = "Debug capture could not be retrieved. No new capture was started.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>定期取得は先頭ページの受付・実行中だけに限ります。</summary>
    /// <returns>画面破棄までの参照処理です。</returns>
    private async Task PollAsync(CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (Active && offset == 0 && !busy)
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(true);
                    StateHasChanged();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 画面を閉じても、Core側で予約した一件はそのまま記録します。
        }
    }

    /// <summary>応答サイズで分割された次のページへ進みます。</summary>
    /// <returns>次の記録です。</returns>
    private Task NextAsync(CancellationToken cancellationToken = default)
    {
        if (state?.NextOffset is int next)
        {
            previous.Push(offset);
            offset = next;
        }
        return RefreshAsync(cancellationToken);
    }

    /// <summary>実際に取得したページの先頭位置へ戻ります。</summary>
    /// <returns>前の記録です。</returns>
    private Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        if (previous.TryPop(out int earlier))
        {
            offset = earlier;
        }
        return RefreshAsync(cancellationToken);
    }

    /// <summary>同じ記録だけを有限ページで読み、保存用JSONへまとめます。</summary>
    /// <returns>ブラウザーへのダウンロード開始です。</returns>
    private async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (state is null)
        {
            return;
        }
        busy = true;
        try
        {
            Guid id = state.Arm.Id;
            List<WorkflowDebugNode> nodes = [];
            int page = 0;
            WorkflowDebugView? snapshot;
            do
            {
                snapshot = await ReadAsync(id, page, cancellationToken).ConfigureAwait(true) ?? throw new HttpRequestException("Capture expired.");
                nodes.AddRange(snapshot.Nodes ?? []);
                page = snapshot.NextOffset ?? -1;
            } while (0 <= page && nodes.Count <= 4096);
            await Script.InvokeVoidAsync("contentsBrowser.download", cancellationToken, "workflow-debug-" + id.ToString("N") + ".json",
                JsonSerializer.Serialize(snapshot with { Nodes = nodes, NextOffset = null }, ContractJson.Options)).ConfigureAwait(true);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException or JSException)
        {
            problem = "Debug capture could not be retrieved. No new capture was started.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>予約を変更しない読取り専用操作です。</summary>
    /// <returns>一ページの記録です。</returns>
    private Task<WorkflowDebugView?> ReadAsync(Guid id, int page, CancellationToken cancellationToken = default)
        => Api.PostForPeerAsync<WorkflowDebugView>("/api/workflow-debug/read", new WorkflowDebugQuery(id, page), peer, cluster, cancellationToken: cancellationToken);

    /// <summary>画面側の参照だけを終了し、実行中の記録を取り消しません。</summary>
    /// <returns>定期参照の終了です。</returns>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await polling.ConfigureAwait(true);
    }
}
