using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Execution;

/// <summary>画面の接続先が変わっても、元の実行とPeer実体へ取消を要求します。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言と同じ型を構成するため。")]
public sealed partial class RunCancelButton
{
    [Inject]
    private OperationJournal Journal
    {
        get; set;
    } = null!;

    /// <summary>元のワークフロー実行。</summary>
    [Parameter]
    public Guid RunId
    {
        get; set;
    }

    /// <summary>元の実行を追跡するプロセス実体。</summary>
    [Parameter]
    public Guid OwnerInstanceId
    {
        get; set;
    }

    /// <summary>元の実行を受け付けたPeer。</summary>
    [Parameter]
    public string? PeerId
    {
        get; set;
    }

    /// <summary>元の実行が属するクラスタ。</summary>
    [Parameter]
    public string? ClusterId
    {
        get; set;
    }

    /// <summary>取消受付後に元の実行結果を再確認する通知。</summary>
    [Parameter]
    public EventCallback Completed
    {
        get; set;
    }

    private bool busy;
    private bool submitted;
    private Func<string>? message;

    /// <summary>取消の要求を保存して一度送り、受付と実停止を区別して案内します。</summary>
    /// <returns>要求の受付確認と、元の実行の再照会通知。</returns>
    private async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (busy || submitted || OwnerInstanceId == Guid.Empty || PeerId is null || ClusterId is null)
        {
            return;
        }
        busy = true;
        WorkflowCancelRequest request = new(Guid.NewGuid(), RunId, OwnerInstanceId);
        string peer = PeerId;
        string cluster = ClusterId;
        try
        {
            submitted = true;
            JsonElement response = await Journal.StartAsync(request.OperationId, "Cancel execution", "/api/runs/cancel", "/api/runs/cancellations/" + request.OperationId,
                request, targetPeer: peer, targetCluster: cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            WorkflowCancelResult? receipt = response.Deserialize<WorkflowCancelResult>(ContractJson.Options);
            message = Ui.Deferred("Cancellation request: {0}. {1}", receipt?.Outcome, Ui[receipt?.Reason ?? "run.cancelUnconfirmed"]);
            await Completed.InvokeAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or Microsoft.JSInterop.JSException)
        {
            message = Ui.Deferred("Cancellation is unconfirmed. The original cancellation ID is retained in operation history.");
        }
        finally
        {
            busy = false;
        }
    }
}
