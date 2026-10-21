using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>同じPeerの保存候補と有効版を比較し、確認対象を親画面へ渡します。</summary>
// 抑制理由: Razorが生成する公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言と同じ型を構成するため。")]
public sealed partial class ConfigurationImpactPanel
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>比較する保存済みワークフローです。</summary>
    [Parameter, EditorRequired]
    public string WorkflowId
    {
        get; set;
    } = string.Empty;

    /// <summary>親画面が表示している保存版です。</summary>
    [Parameter, EditorRequired]
    public string Revision
    {
        get; set;
    } = string.Empty;

    /// <summary>共通運用設定の比較を取得するか。</summary>
    [Parameter]
    public bool Operational
    {
        get; set;
    }

    /// <summary>確認対象を取得できないときはnullを通知します。</summary>
    [Parameter]
    public EventCallback<ConfigurationImpactPreview?> Changed
    {
        get; set;
    }

    private ConfigurationImpactPreview? report;
    private Func<string>? problem;
    private bool busy;
    private string? cluster, peer;

    /// <summary>画面が参照するPeerを固定し、同じ版の比較を取得します。</summary>
    /// <returns>比較結果の取得完了です。</returns>
    // 抑制理由: Blazorの固定署名に従い、通信には画面の取消トークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名。通信にはUiCancellationを渡します。")]
    protected override Task OnInitializedAsync()
    {
        cluster = Api.SelectedClusterId;
        peer = Api.SelectedPeerId;
        return LoadAsync(UiCancellation);
    }

    /// <summary>旧確認を先に解除し、対象が一致する比較だけを反映に利用します。</summary>
    /// <returns>表示と親画面への確認対象通知です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        await Changed.InvokeAsync(null).ConfigureAwait(true);
        try
        {
            string path = "/api/workflows/" + Uri.EscapeDataString(WorkflowId) + "/impact";
            if (Operational)
            {
                path = "/api/configuration/impact";
            }
            report = await Api.GetForPeerAsync<ConfigurationImpactPreview>(path, peer, cluster, cancellationToken).ConfigureAwait(true);
            if (report is null || !string.Equals(report.TargetId, WorkflowId, StringComparison.Ordinal) || !string.Equals(report.SavedRevision, Revision, StringComparison.Ordinal))
            {
                report = null;
                problem = Ui.Deferred("The saved version changed. Reload the workflow before applying.");
                return;
            }
            problem = null;
            await Changed.InvokeAsync(report).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            report = null;
            problem = Ui.Deferred("Impact information could not be confirmed.");
        }
        finally
        {
            busy = false;
        }
    }
}
