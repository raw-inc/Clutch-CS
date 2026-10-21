using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Scripting;

/// <summary>導入受付と進捗照会を分け、モーダルを閉じても元操作を再実行しません。</summary>
// 抑制理由: Razor生成コンポーネントの公開宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開宣言に合わせます。")]
public sealed partial class ScriptRuntimeInstallPanel
{
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

    /// <summary>親がkeyで固定する導入言語です。</summary>
    [Parameter]
    public string Language
    {
        get; set;
    } = "python";

    /// <summary>導入先のPeerです。</summary>
    [Parameter]
    public string? PeerId
    {
        get; set;
    }

    /// <summary>導入先のクラスタです。</summary>
    [Parameter]
    public string? ClusterId
    {
        get; set;
    }

    /// <summary>他の導入中に新しい受付を開始しません。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>成功した既定変更を同じ画面の選択肢へ反映します。</summary>
    [Parameter]
    public EventCallback DefaultUpdated
    {
        get; set;
    }

    private bool open, busy;
    private string version = string.Empty;
    private string? problem;
    private ScriptRuntimeCatalog? catalog;
    private ScriptRuntimeInstallStatus? status;
    private readonly List<ScriptRuntimeRelease> releases = [];
    private Guid? watched;
    private Task polling = Task.CompletedTask;

    /// <summary>導入操作を明示したときだけ公式版一覧を取得します。</summary>
    /// <returns>版選択と既存操作の表示です。</returns>
    // 抑制理由: 監視Taskをフィールドで所有し、DisposeAsyncで終了確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有する監視Taskを終了時にawaitします。")]
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (PeerId is null || ClusterId is null || Disabled || busy)
        {
            return;
        }
        open = true;
        busy = true;
        problem = null;
        try
        {
            catalog = await Api.PostForPeerAsync<ScriptRuntimeCatalog>("/api/scripts/runtimes/list", new ScriptRuntimeQuery(Language), PeerId, ClusterId, cancellationToken: cancellationToken).ConfigureAwait(true);
            ScriptRuntimeRelease[] received = await Api.PostForPeerAsync<ScriptRuntimeRelease[]>("/api/scripts/runtimes/releases", new ScriptRuntimeQuery(Language), PeerId, ClusterId, cancellationToken: cancellationToken).ConfigureAwait(true) ?? [];
            releases.Clear();
            releases.AddRange(received);
            version = string.Empty;
            if (releases.Count != 0)
            {
                version = releases[0].Version;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            problem = "Official runtime versions could not be retrieved. Installation currently supports Windows x64 / ARM64.";
        }
        finally
        {
            busy = false;
        }
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        if (polling.IsCompleted)
        {
            polling = PollAsync(UiCancellation);
        }
    }

    /// <summary>送信前に識別子と要求を保持し、応答不明時は照会だけを続けます。</summary>
    /// <returns>元操作の受付確認です。</returns>
    private async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (busy || Disabled || status?.Busy == true || version.Length == 0 || PeerId is null || ClusterId is null)
        {
            return;
        }
        Guid id = Guid.NewGuid();
        watched = id;
        busy = true;
        problem = null;
        try
        {
            await Journal.StartAsync(id, "script-runtime", "/api/scripts/runtimes/install", "/api/scripts/runtimes/operations/" + id,
                new ScriptRuntimeInstallRequest(id, Language, version), targetPeer: PeerId, targetCluster: ClusterId, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or JSException or OperationCanceledException)
        {
            problem = "Runtime installation could not be confirmed. Query its original operation from Operations; do not resend it.";
        }
        finally
        {
            busy = false;
        }
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>少量の進捗メタデータだけを取得し、バイナリーをClientへ転送しません。</summary>
    /// <returns>表示と成功通知の更新です。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (busy || PeerId is null || ClusterId is null)
        {
            return;
        }
        busy = true;
        try
        {
            status = await Api.GetForPeerAsync<ScriptRuntimeInstallStatus>("/api/scripts/runtimes/installations", PeerId, ClusterId, cancellationToken).ConfigureAwait(true);
            ScriptRuntimeInstallOperation? operation = status?.Operations.FirstOrDefault(item => item.Request.OperationId == watched);
            if (operation is { Completed: true })
            {
                watched = null;
                if (operation.DefaultApplied)
                {
                    await DefaultUpdated.InvokeAsync().ConfigureAwait(true);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            problem = "Runtime installation status could not be retrieved. Refresh to query the original operations.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>開いている間、または自分の受付が未完了の間だけ進捗を照会します。</summary>
    /// <returns>画面終了までの有限間隔監視です。</returns>
    private async Task PollAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
            while ((open || watched is not null) && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 画面の終了は導入操作の所有権を持つCoreの取消にはなりません。
        }
    }

    /// <summary>画面の監視を残さず終了します。</summary>
    /// <returns>所有Taskの完了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "基底クラスの取消と固定署名に合わせます。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await polling.ConfigureAwait(true);
    }
}
