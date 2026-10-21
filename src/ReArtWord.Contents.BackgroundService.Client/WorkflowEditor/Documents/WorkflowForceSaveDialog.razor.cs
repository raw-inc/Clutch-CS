using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>草稿の退避と最新保存版の差分を示してから、利用者の明示上書きを実行します。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor生成コードの公開宣言に合わせます。")]
public sealed partial class WorkflowForceSaveDialog
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
    private BrowserStorage Storage
    {
        get; set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get; set;
    } = null!;

    [Inject]
    private EditorCollaboration Collaboration
    {
        get; set;
    } = null!;

    /// <summary>確認画面の表示状態です。</summary>
    [Parameter]
    public bool Open
    {
        get; set;
    }

    /// <summary>通常保存と同じ反映方針です。</summary>
    [Parameter]
    public EActivationPolicy Activation
    {
        get; set;
    }

    /// <summary>確認画面を閉じます。</summary>
    [Parameter]
    public EventCallback Close
    {
        get; set;
    }

    /// <summary>送信中の重複保存と画面編集を止めます。</summary>
    [Parameter]
    public EventCallback<bool> BusyChanged
    {
        get; set;
    }

    /// <summary>確認結果を保存一覧と操作表示へ渡します。</summary>
    [Parameter]
    public EventCallback<WorkflowSaveResult> Saved
    {
        get; set;
    }

    private EditorForceSave? force;
    private bool opened, working;
    private string? problem;

    /// <summary>開くたびに最新保存版を取得し、結果不明の元要求は残します。</summary>
    /// <returns>確認情報の取得です。</returns>
    protected override async Task OnParametersSetAsync()
    {
        bool prepare = Open && !opened;
        opened = Open;
        if (prepare)
        {
            force ??= new(Model, Api, Storage, Journal);
            await PrepareAsync(UiCancellation).ConfigureAwait(true);
        }
    }

    /// <summary>照会の待ち時間を有限にし、現在草稿を上書きせず比較します。</summary>
    /// <returns>差分表示の準備です。</returns>
    private async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (working)
        {
            return;
        }
        working = true;
        problem = null;
        await BusyChanged.InvokeAsync(true).ConfigureAwait(true);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await Collaboration.WaitForIdleAsync(timeout.Token).ConfigureAwait(true);
            await force!.PrepareAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or JSException or OperationCanceledException or TimeoutException)
        {
            problem = Ui.DeferredException(error)();
        }
        finally
        {
            working = false;
            await BusyChanged.InvokeAsync(false).ConfigureAwait(true);
        }
    }

    /// <summary>送信または元操作の照会を行い、送信済み要求を新規IDで再実行しません。</summary>
    /// <returns>操作結果の表示です。</returns>
    private async Task SubmitAsync(bool lookup, CancellationToken cancellationToken = default)
    {
        if (working || force is null)
        {
            return;
        }
        working = true;
        problem = null;
        await BusyChanged.InvokeAsync(true).ConfigureAwait(true);
        try
        {
            WorkflowSaveResult? result = lookup ? await force.LookupAsync(cancellationToken).ConfigureAwait(true)
                                                : await force.SaveAsync(Activation, cancellationToken).ConfigureAwait(true);
            if (result is not null)
            {
                await Model.PersistAsync(cancellationToken).ConfigureAwait(true);
                await Saved.InvokeAsync(result).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or JSException or OperationCanceledException)
        {
            problem = Ui.DeferredException(error)();
        }
        finally
        {
            working = false;
            await BusyChanged.InvokeAsync(false).ConfigureAwait(true);
        }
    }

    /// <summary>手元の草稿と上書き前の保存版を個別に退避し、書出し失敗も表示します。</summary>
    /// <returns>ダウンロード要求です。</returns>
    private async Task ExportAsync(bool saved, CancellationToken cancellationToken = default)
    {
        ReArtWord.Contents.BackgroundService.Contracts.Workflow.WorkflowDefinition? definition = force?.Review?.Draft.Document ?? Model.Draft.Document;
        string suffix = ".json";
        if (saved)
        {
            definition = force?.Review?.Saved?.Definition;
            suffix = ".saved.json";
        }
        try
        {
            if (definition is not null)
            {
                await Storage.DownloadAsync(definition.Id + suffix, definition, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (error is JSException or OperationCanceledException)
        {
            problem = Ui.DeferredException(error)();
        }
    }

    /// <summary>送信中は対象の確認画面を維持します。</summary>
    /// <returns>親画面への終了通知です。</returns>
    private Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return working ? Task.CompletedTask
                       : Close.InvokeAsync();
    }
}
