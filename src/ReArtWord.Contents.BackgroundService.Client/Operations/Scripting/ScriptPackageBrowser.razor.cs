using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Scripting;

/// <summary>検索・版の選択と、利用者が押した導入操作を分離します。</summary>
// 抑制理由: Razorが生成する公開partialの宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor生成宣言に合わせます。")]
public sealed partial class ScriptPackageBrowser
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>表示している公開配布方式です。親が方式ごとにインスタンスを分けます。</summary>
    [Parameter]
    public EScriptPackageKind Kind
    {
        get; set;
    }

    /// <summary>親が固定したPeerでの導入可否です。</summary>
    [Parameter]
    public bool Busy
    {
        get; set;
    }

    /// <summary>同じPeerで新しい解析に使用している直接依存です。</summary>
    // 抑制理由: Blazorのパラメーター受信です。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "BlazorのParameterとして受信します。")]
    public IReadOnlyList<ScriptPackageReference> Installed
    {
        get; set;
    } = [];

    /// <summary>確定したパッケージ名と版を既存の導入処理へ渡します。</summary>
    [Parameter]
    public EventCallback<ScriptPackageReference> Install
    {
        get; set;
    }

    private string query = string.Empty, searched = string.Empty, selectedVersion = string.Empty;
    private int offset, searchGeneration, detailGeneration;
    private bool searching, selecting;
    private ScriptPackageSearchResult? results;
    private ScriptPackageDetails? details;

    // 抑制理由: 各照会のusingが所有・破棄し、フィールドは再検索時の取消用参照だけを保持します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "照会メソッドのusingがCTSを所有し、非同期照会終了前にフィールド側では破棄しません。")]
    private CancellationTokenSource? searchLifetime, detailLifetime;

    /// <summary>大文字小文字やPython名の区切りが違っても同じ導入済み項目へ対応付けます。</summary>
    /// <returns>このPeerでの導入版かnullです。</returns>
    private string? InstalledVersion(string id) => Installed.FirstOrDefault(package => string.Equals(ScriptPackageNames.Normalize(package.Id, Kind), ScriptPackageNames.Normalize(id, Kind), StringComparison.Ordinal))?.Version;

    /// <summary>古い検索と詳細照会を取り消し、現在の条件の結果だけを表示します。</summary>
    /// <returns>一ページの検索結果です。</returns>
    private async Task SearchAsync(string text, int pageOffset, CancellationToken cancellationToken = default)
    {
        if (ComponentDisposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        ValueTask cancelSearch = new(searchLifetime?.CancelAsync() ?? Task.CompletedTask);
        ValueTask cancelDetail = new(detailLifetime?.CancelAsync() ?? Task.CompletedTask);
        detailLifetime = null;
        detailGeneration++;
        details = null;
        selecting = false;
        selectedVersion = string.Empty;
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UiCancellation);
        searchLifetime = request;
        int generation = ++searchGeneration;
        searching = true;
        results = null;
        searched = text.Trim();
        offset = pageOffset;
        try
        {
            await Task.WhenAll(cancelSearch.AsTask(), cancelDetail.AsTask()).ConfigureAwait(true);
            request.Token.ThrowIfCancellationRequested();
            ScriptPackageSearchResult? received = await Api.PostAsync<ScriptPackageSearchResult>("/client/script-packages/search", new ScriptPackageSearchRequest(Kind, searched, offset), request.Token).ConfigureAwait(true);
            if (generation == searchGeneration && !request.IsCancellationRequested)
            {
                results = received ?? new([], false, Problem: "script.packages.searchUnavailable");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (generation == searchGeneration && !request.IsCancellationRequested)
            {
                results = new([], false, Problem: "script.packages.searchUnavailable");
            }
        }
        finally
        {
            if (ReferenceEquals(searchLifetime, request))
            {
                searchLifetime = null;
            }
            if (generation == searchGeneration)
            {
                searching = false;
            }
        }
    }

    /// <summary>候補の選択だけではインストールを開始せず、公開版を取得します。</summary>
    /// <returns>現在選択中のパッケージ詳細です。</returns>
    private async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        if (ComponentDisposed)
        {
            return;
        }
        ValueTask cancelPrior = new(detailLifetime?.CancelAsync() ?? Task.CompletedTask);
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UiCancellation);
        detailLifetime = request;
        int generation = ++detailGeneration;
        selecting = true;
        details = null;
        selectedVersion = string.Empty;
        try
        {
            await cancelPrior.ConfigureAwait(true);
            request.Token.ThrowIfCancellationRequested();
            ScriptPackageDetails? received = await Api.PostAsync<ScriptPackageDetails>("/client/script-packages/details", new ScriptPackageDetailsRequest(Kind, id), request.Token).ConfigureAwait(true);
            if (generation == detailGeneration && !request.IsCancellationRequested)
            {
                details = received ?? new(id, string.Empty, string.Empty, [], null, Problem: "script.packages.detailsUnavailable");
                selectedVersion = details.LatestVersion;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (generation == detailGeneration && !request.IsCancellationRequested)
            {
                details = new(id, string.Empty, string.Empty, [], null, Problem: "script.packages.detailsUnavailable");
            }
        }
        finally
        {
            if (ReferenceEquals(detailLifetime, request))
            {
                detailLifetime = null;
            }
            if (generation == detailGeneration)
            {
                selecting = false;
            }
        }
    }

    /// <summary>配布方式やPeerを切り替えた後に検索応答を適用しません。</summary>
    /// <returns>所有する照会の取消です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI寿命の終了です。")]
    public override async ValueTask DisposeAsync()
    {
        searchGeneration++;
        detailGeneration++;
        await base.DisposeAsync().ConfigureAwait(true);
        searchLifetime = null;
        detailLifetime = null;
    }
}
