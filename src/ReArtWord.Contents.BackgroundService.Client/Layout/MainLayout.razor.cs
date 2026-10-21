using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Reflection;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

namespace ReArtWord.Contents.BackgroundService.Client.Layout;

/// <summary>共通ナビゲーションと表示言語・接続状態を維持します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class MainLayout : IAsyncDisposable
{
    [Inject]
    private DisplayTimePreferences DisplayTime
    {
        get; set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get;
        set;
    } = null!;

    [Inject]
    private Overview.Updates.FleetUpdateProgress Fleet
    {
        get; set;
    } = null!;

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private BrowserStorage Storage
    {
        get;
        set;
    } = null!;

    [Inject]
    private NavigationManager Navigation
    {
        get;
        set;
    } = null!;

    [Inject]
    private UiText Ui
    {
        get;
        set;
    } = null!;

    [Inject]
    private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private bool initialized;
    private string? visibleNavigation;
    private int transferCount;
    private bool transferCountUnconfirmed = true;
    private TransferProgressPopup? transfers;
    private static string FullUiVersion
    {
        get;
    } = typeof(MainLayout).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static string ShortUiVersion
    {
        get;
    } = AbbreviateVersion(FullUiVersion);

    /// <summary>表示用にはコミットを8桁までにし、完全な版はツールチップに残します。</summary>
    /// <returns>読み込まれたUI版の短い表記です。</returns>
    private static string AbbreviateVersion(string version)
    {
        int marker = version.IndexOf('+', StringComparison.Ordinal);
        if (marker < 0)
        {
            return version;
        }
        return version[..Math.Min(version.Length, marker + 9)];
    }

    private Contracts.Management.ClientBranding branding = new();
    private bool IsRemoteTools => new Uri(Navigation.Uri).AbsolutePath.TrimEnd('/') is "/terminal" or "/file-transfer" or "/browser-transfer" or "/desktop" or "/licenses" or "/host-manager" or "/signal-scope";
    private bool IsEditor => new Uri(Navigation.Uri).AbsolutePath.TrimEnd('/').EndsWith("/editor", StringComparison.OrdinalIgnoreCase);
    private bool IsDashboard => new Uri(Navigation.Uri).AbsolutePath.TrimEnd('/') is "" or "/dashboard";
    private bool IsLogViewer => string.Equals(new Uri(Navigation.Uri).AbsolutePath.TrimEnd('/'), "/logs", StringComparison.OrdinalIgnoreCase);

    /// <summary>ログからのクラスタ移動を本文の生成前に反映し、読込み途中のEditorを作り直しません。</summary>
    /// <returns>記録元の接続先とブラウザー選択の更新です。</returns>
    // 抑制理由: Blazorの固定署名で、レイアウトの存続期間をI/Oへ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でlifetime.Tokenを使用します。")]
    protected override async Task OnParametersSetAsync()
    {
        if (!initialized || !IsEditor || EditorWorkflowLocation.Read(Navigation.Uri) is not { ClusterId: { Length: > 0 } cluster, PeerId: { Length: > 0 } peer }
            || (string.Equals(Api.SelectedClusterId, cluster, StringComparison.Ordinal) && string.Equals(Api.SelectedPeerId, peer, StringComparison.Ordinal)))
        {
            return;
        }
        await Connection.SelectTargetAsync(cluster, peer, lifetime.Token).ConfigureAwait(true);
        try
        {
            await Storage.WriteAsync("contents.client.cluster", cluster, true, lifetime.Token).ConfigureAwait(true);
            await Storage.WriteAsync("contents.client.peer", peer, true, lifetime.Token).ConfigureAwait(true);
        }
        catch (JSException)
        {
            // 保存できなくても、今回の明示された移動先は維持します。
        }
    }

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override async Task OnInitializedAsync()
    {
        Ui.Changed += LanguageChanged;
        DisplayTime.Changed += LanguageChanged;
        await Ui.InitializeAsync(lifetime.Token).ConfigureAwait(true);
        await DisplayTime.InitializeAsync(lifetime.Token).ConfigureAwait(true);
        try
        {
            branding = await Api.GetAsync<Contracts.Management.ClientBranding>("/client/branding", lifetime.Token).ConfigureAwait(true) ?? new();
            await Script.InvokeVoidAsync("contentsBranding.setFavicon", lifetime.Token, branding.Favicon ?? branding.HeaderLogo).ConfigureAwait(true);
        }
        catch (HttpRequestException)
        {
            // オフラインのキャッシュ画面でも既定の製品表示を利用できます。
        }
        try
        {
            string? cluster = await Storage.ReadAsync<string>("contents.client.cluster", true, lifetime.Token).ConfigureAwait(true);
            string? peer = await Storage.ReadAsync<string>("contents.client.peer", true, lifetime.Token).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(cluster) && !string.IsNullOrWhiteSpace(peer))
            {
                await Connection.SelectTargetAsync(cluster, peer, lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (JSException)
        {
            // 接続選択UIは端末保存が使えない場合にも残します。
        }

        initialized = true;
        await Fleet.RefreshAsync(lifetime.Token).ConfigureAwait(true);
        polling = PollAsync();
    }

    /// <summary>表示言語の変更を共通レイアウトの再描画へ通知します。</summary>
    // 抑制理由: 同期イベントで開始する描画Taskをフィールドで保持し、終了時にawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "言語変更の描画TaskはlanguageRefreshで保持し、DisposeAsyncで完了を確認します。")]
    private void LanguageChanged()
    {
        if (!lifetime.IsCancellationRequested && languageRefresh.IsCompletedSuccessfully)
        {
            languageRefresh = InvokeAsync(StateHasChanged);
        }
    }

    private Task languageRefresh = Task.CompletedTask;

    /// <summary>移動時だけ選択中のヘッダータブを表示し、手動スクロールや本文位置を変えません。</summary>
    /// <returns>ナビゲーションの表示調整です。</returns>
    // 抑制理由: Blazorの固定署名で、画面寿命の取消しをブラウザー呼出しへ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でlifetime.Tokenを使用します。")]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!string.Equals(visibleNavigation, Navigation.Uri, StringComparison.Ordinal))
        {
            await Script.InvokeVoidAsync("contentsBrowser.revealNavigation", lifetime.Token).ConfigureAwait(true);
            visibleNavigation = Navigation.Uri;
        }
    }

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true))
            {
                await Fleet.RefreshAsync(lifetime.Token).ConfigureAwait(true);
                await Connection.RefreshAsync(lifetime.Token).ConfigureAwait(true);
                try
                {
                    transferCount = await Api.GetAsync<int>("/client/transfers/active-count", lifetime.Token).ConfigureAwait(true);
                    transferCountUnconfirmed = false;
                }
                catch (HttpRequestException)
                {
                    transferCountUnconfirmed = true;
                }
                if (transfers is not null)
                {
                    await transfers.RefreshAsync(lifetime.Token).ConfigureAwait(true);
                }
                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>画面が所有する継続処理を終了し、保持する参照と資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async ValueTask DisposeAsync()
    {
        try
        {
            Ui.Changed -= LanguageChanged;
            DisplayTime.Changed -= LanguageChanged;
            await lifetime.CancelAsync().ConfigureAwait(true);
            await polling.ConfigureAwait(true);
            await languageRefresh.ConfigureAwait(true);
        }
        finally
        {
            lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
