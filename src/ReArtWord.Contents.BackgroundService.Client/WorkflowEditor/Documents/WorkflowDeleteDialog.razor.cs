using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>確認した対象と版を固定し、結果不明の削除を再送しません。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class WorkflowDeleteDialog
{
    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get; set;
    } = null!;

    /// <summary>確認画面の表示状態です。</summary>
    [Parameter]
    public bool Open
    {
        get; set;
    }

    /// <summary>確認画面を閉じます。</summary>
    [Parameter]
    public EventCallback Close
    {
        get; set;
    }

    /// <summary>共有削除が確定した対象だけを親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<string> Deleted
    {
        get; set;
    }

    private string id = string.Empty, name = string.Empty;
    private string? revision, cluster, peer, problem;
    private bool opened, working, submitted, dirty, localOnly;
    private int generation;
    private WorkflowDeleteResult? result;

    /// <summary>確認画面を開いた時点の対象を保持します。</summary>
    protected override void OnParametersSet()
    {
        if (Open && !opened)
        {
            id = Model.Draft.Document.Id;
            name = Model.Draft.Document.Name;
            revision = Model.Draft.BaseRevision;
            dirty = Model.Draft.Dirty || Model.HasInvalidInputs;
            localOnly = Model.Draft.LocalOnly;
            cluster = Api.SelectedClusterId;
            peer = Api.SelectedPeerId;
            generation = Model.Generation;
            result = null;
            problem = null;
            submitted = false;
        }
        opened = Open;
    }

    /// <summary>確定前に対象を再照合し、保存版の競合はサーバーでも拒否します。</summary>
    /// <returns>削除受付または失敗理由です。</returns>
    private async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (working || submitted || localOnly)
        {
            return;
        }
        if (generation != Model.Generation || !string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal) || !string.Equals(peer, Api.SelectedPeerId, StringComparison.Ordinal))
        {
            problem = Ui["The connection or workflow changed. Reopen the deletion dialog."];
            return;
        }
        working = true;
        try
        {
            if (revision is not null)
            {
                submitted = true;
                Guid operation = Guid.NewGuid();
                JsonElement response = await Journal.StartAsync(operation, Ui["Delete workflow"], "/api/workflows/delete", "/api/workflows/deletions/" + operation,
                    new WorkflowDeleteRequest(operation, id, revision), cancellationToken: cancellationToken).ConfigureAwait(true);
                result = response.Deserialize<WorkflowDeleteResult>(ContractJson.Options);
                if (result?.Deleted.Outcome != EOutcome.Succeeded)
                {
                    return;
                }
            }
            if (generation == Model.Generation && string.Equals(cluster, Model.ClusterId, StringComparison.Ordinal))
            {
                await Deleted.InvokeAsync(id).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or Microsoft.JSInterop.JSException)
        {
            problem = Ui["The deletion result could not be confirmed. Check the original operation in History and result lookup."];
        }
        finally
        {
            working = false;
        }
    }

    /// <summary>送信中は確認画面を維持します。</summary>
    /// <returns>親への終了通知です。</returns>
    private Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return working ? Task.CompletedTask
                       : Close.InvokeAsync();
    }
}
