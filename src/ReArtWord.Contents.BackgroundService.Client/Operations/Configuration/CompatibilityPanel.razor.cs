using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>保存版・有効版・スナップショットの互換性を、変更せずに確認する画面です。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言と同じ型を構成するため。")]
public sealed partial class CompatibilityPanel
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>確認する保存済みワークフロー。</summary>
    [Parameter, EditorRequired]
    public string WorkflowId
    {
        get; set;
    } = string.Empty;

    private WorkflowCompatibilityReport? report;
    private Func<string>? problem;
    private bool busy;
    private string? cluster, peer;
    private string workflow = string.Empty;

    /// <summary>画面を開いたときの対象を固定して確認を始めます。</summary>
    /// <returns>最初の検査結果の取得。</returns>
    // 抑制理由: Blazorの固定署名に従い、通信には画面の取消トークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名。通信にはUiCancellationを渡します。")]
    protected override Task OnInitializedAsync()
    {
        cluster = Api.SelectedClusterId;
        peer = Api.SelectedPeerId;
        workflow = WorkflowId;
        return LoadAsync(null, UiCancellation);
    }

    /// <summary>同じワークフロー・Peerの続きだけを取得します。</summary>
    /// <returns>検査結果の表示。</returns>
    private async Task LoadAsync(string? after, CancellationToken cancellationToken = default)
    {
        if (busy || peer is null || cluster is null)
        {
            return;
        }
        busy = true;
        try
        {
            report = await Api.PostForPeerAsync<WorkflowCompatibilityReport>("/api/workflows/compatibility", new WorkflowCompatibilityQuery(workflow, after),
                peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            problem = null;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            report = null;
            problem = Ui.Deferred("Compatibility could not be confirmed for the selected peer.");
        }
        finally
        {
            busy = false;
        }
    }
}
