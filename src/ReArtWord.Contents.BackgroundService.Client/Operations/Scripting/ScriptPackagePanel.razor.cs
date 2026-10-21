using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Scripting;

/// <summary>選択したPeerへ一度だけ導入要求を送り、結果は読取りで追跡します。</summary>
// 抑制理由: Razorが生成する公開コンポーネント宣言に合わせます。
[SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partial宣言と同じ型です。")]
public sealed partial class ScriptPackagePanel
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

    private ScriptPackageCatalog? catalog;
    private EScriptPackageKind kind;
    private string packageId = string.Empty, version = string.Empty, pythonPath = string.Empty, nodePath = string.Empty, npmPath = string.Empty;
    private string? peer, cluster;
    private bool loading, sending;
    private Func<string>? message;
    private Task polling = Task.CompletedTask;
    private int runtimeGeneration;

    private bool Busy => loading || sending || catalog is null || catalog.Busy;

    /// <summary>同じ画面から追加した実行環境を次のパッケージ操作の既定として選択します。</summary>
    private void RuntimeInstalled()
    {
        if (kind == EScriptPackageKind.Pip)
        {
            pythonPath = string.Empty;
        }
        else
        {
            nodePath = string.Empty;
            npmPath = string.Empty;
        }
        runtimeGeneration++;
    }

    /// <summary>取得元を固定し、親のkeyが変わるまで別Peerへ要求を送信しません。</summary>
    /// <returns>初回取得と結果監視の開始です。</returns>
    // 抑制理由: Blazorの固定署名です。監視Taskを保持し、DisposeAsyncで取消・awaitします。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名でUiCancellationを使います。"),
     SuppressMessage("SourceQuality", "RAW511", Justification = "監視Taskをフィールドで保持し、終了時にawaitします。")]
    protected override async Task OnInitializedAsync()
    {
        peer = Api.SelectedPeerId;
        cluster = Api.SelectedClusterId;
        await RefreshAsync(UiCancellation).ConfigureAwait(true);
        polling = PollAsync(UiCancellation);
    }

    /// <summary>画面の世代に結び付いた構成だけを参照します。</summary>
    /// <returns>配布方式ごとの構成です。</returns>
    private ScriptPackageEnvironmentInfo? Current(EScriptPackageKind source)
    {
        if (source == EScriptPackageKind.Pip)
        {
            return catalog?.Pip;
        }
        if (source == EScriptPackageKind.Npm)
        {
            return catalog?.Npm;
        }
        return catalog?.Nuget;
    }

    /// <summary>言語と配布方式の対応を同じ表示名で案内します。</summary>
    /// <returns>パッケージ欄の名前です。</returns>
    private static string SourceName(EScriptPackageKind source) => source switch
    {
        EScriptPackageKind.Nuget => "NuGet · C#",
        EScriptPackageKind.Pip => "pip · Python",
        _ => "npm · JavaScript",
    };

    /// <summary>結果照会で新しいインストールを開始しません。</summary>
    /// <returns>現在構成と操作結果の表示です。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (loading || peer is null || cluster is null)
        {
            return;
        }
        loading = true;
        try
        {
            catalog = await Api.GetForPeerAsync<ScriptPackageCatalog>("/api/scripts/packages", peer, cluster, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            catalog = null;
            message = Ui.Deferred("Package status could not be retrieved. Refresh to query the original operations.");
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>元要求をブラウザーへ記録してから、固定した相手と構成版へ一度だけ送ります。</summary>
    /// <returns>受付の確認と現在状態の再取得です。</returns>
    private async Task ChangeAsync(EScriptPackageKind source, ScriptPackageReference package, bool remove, CancellationToken cancellationToken = default)
    {
        if (Busy)
        {
            return;
        }
        sending = true;
        Guid id = Guid.NewGuid();
        string? executable = null;
        if (source == EScriptPackageKind.Pip && !string.IsNullOrWhiteSpace(pythonPath))
        {
            executable = pythonPath.Trim();
        }
        ScriptPackageChangeRequest request = new(id, source, package, remove, Current(source)?.Revision, executable);
        if (source == EScriptPackageKind.Npm)
        {
            request = request with { NodePath = nodePath.Trim(), NpmPath = npmPath.Trim() };
        }
        try
        {
            await Journal.StartAsync(id, "script-package", "/api/scripts/packages/change", "/api/scripts/packages/operations/" + id, request,
                targetPeer: peer, targetCluster: cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            message = Ui.Deferred("Package operation {0} was received. Check its completion below.", id);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or JSException or OperationCanceledException)
        {
            message = Ui.Deferred("Package operation {0} could not be confirmed. Query the original operation from Operations; do not resend it.", id);
        }
        finally
        {
            sending = false;
        }
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>NuGetの診断コードを維持し、理由部分だけを翻訳します。</summary>
    /// <returns>利用者向けの段階別診断です。</returns>
    private string Reason(string? reason)
    {
        if (reason is null)
        {
            return string.Empty;
        }
        string[] parts = reason.Split(": ", 2, StringSplitOptions.None);
        if (parts.Length == 1)
        {
            return Ui[reason];
        }
        return Ui[parts[0]] + ": " + parts[1];
    }

    /// <summary>画面を開いている間だけ、Peerの導入状況を読取りで監視します。</summary>
    /// <returns>画面終了までの監視です。</returns>
    private async Task PollAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 画面の終了に伴う通常停止です。導入処理の所有者はCore側に残ります。
        }
    }

    /// <summary>タブ変更後の応答・描画を残しません。</summary>
    /// <returns>画面の監視終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "基底コンポーネントが所有する取消を使用します。")]
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(true);
        await polling.ConfigureAwait(true);
    }
}
