using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Scripting;

/// <summary>指定Peerの実行環境だけを遅延取得し、登録と草稿の選択を分けます。</summary>
// 抑制理由: Razorの生成宣言と同じ公開コンポーネントです。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開宣言に合わせます。")]
public sealed partial class ScriptRuntimePicker
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>PythonまたはJavaScriptです。</summary>
    [Parameter, EditorRequired]
    public string Language
    {
        get; set;
    } = "python";

    /// <summary>実行先のPeerです。</summary>
    [Parameter]
    public string? PeerId
    {
        get; set;
    }

    /// <summary>要求先を固定するクラスタです。</summary>
    [Parameter]
    public string? ClusterId
    {
        get; set;
    }

    /// <summary>空欄は既定環境、非空は従来の明示パスです。</summary>
    [Parameter]
    public string Value
    {
        get; set;
    } = string.Empty;

    /// <summary>明示パスまたは既定選択を通知します。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get; set;
    }

    /// <summary>パッケージ管理画面で既定変更を公開します。</summary>
    [Parameter]
    public bool Manage
    {
        get; set;
    }

    /// <summary>編集中の操作制限です。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    private ScriptRuntimeCatalog? catalog;
    private string manual = string.Empty;
    private string? identity, problem;
    private bool busy, browse;

    /// <summary>対象変更では他PCの候補を残さず、Node描画だけでは探索を開始しません。</summary>
    protected override void OnParametersSet()
    {
        string current = ClusterId + "/" + PeerId + "/" + Language;
        if (!string.Equals(identity, current, StringComparison.Ordinal))
        {
            identity = current;
            catalog = null;
            problem = null;
            manual = Value;
            browse = false;
        }
    }

    /// <summary>選択中の言語とPCにだけ遅延応答を適用します。</summary>
    /// <returns>候補一覧の更新です。</returns>
    private async Task LoadAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        if (busy || Disabled || !refresh && catalog is not null)
        {
            return;
        }
        if (PeerId is null || ClusterId is null)
        {
            problem = "Select one execution peer to verify these choices.";
            return;
        }
        string? captured = identity;
        busy = true;
        problem = null;
        try
        {
            ScriptRuntimeCatalog? result = await Api.PostForPeerAsync<ScriptRuntimeCatalog>("/api/scripts/runtimes/list", new ScriptRuntimeQuery(Language, refresh), PeerId, ClusterId, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (string.Equals(identity, captured, StringComparison.Ordinal))
            {
                catalog = result;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (string.Equals(identity, captured, StringComparison.Ordinal))
            {
                problem = "Runtime choices could not be retrieved. Check the execution peer and refresh.";
            }
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>使用可能な保存済み候補か既定選択だけを草稿へ通知します。</summary>
    /// <returns>親の選択反映です。</returns>
    private Task SelectAsync(ChangeEventArgs arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Disabled)
        {
            return Task.CompletedTask;
        }
        return ValueChanged.InvokeAsync(arguments.Value?.ToString() ?? string.Empty);
    }

    /// <summary>ファイルの閲覧は実行環境登録や既定変更を開始しません。</summary>
    /// <returns>入力欄の更新です。</returns>
    private Task PickedAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        manual = path;
        browse = false;
        return Task.CompletedTask;
    }

    /// <summary>実行先で版を確認してから登録し、成功時だけ選択値へ反映します。</summary>
    /// <returns>登録結果です。</returns>
    private async Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        string path = manual.Trim();
        if (await ChangeAsync(path, false, cancellationToken).ConfigureAwait(true))
        {
            await ValueChanged.InvokeAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>明示操作でPeerの既定を変更します。空欄は自動検出へ戻します。</summary>
    /// <returns>既定の変更確認です。</returns>
    private async Task SetDefaultAsync(CancellationToken cancellationToken = default)
        => _ = await ChangeAsync(Value, true, cancellationToken).ConfigureAwait(true);

    /// <summary>変更前の設定版を渡し、他の画面が変更した既定を上書きしません。</summary>
    /// <returns>同じ画面世代への確定応答を確認できたかです。</returns>
    private async Task<bool> ChangeAsync(string path, bool setDefault, CancellationToken cancellationToken = default)
    {
        if (busy || Disabled || PeerId is null || ClusterId is null)
        {
            return false;
        }
        await LoadAsync(false, cancellationToken).ConfigureAwait(true);
        if (catalog is null)
        {
            return false;
        }
        string? captured = identity;
        busy = true;
        problem = null;
        try
        {
            ScriptRuntimeCatalog? result = await Api.PostForPeerAsync<ScriptRuntimeCatalog>("/api/scripts/runtimes/change",
                new ScriptRuntimeChange(Language, path, setDefault, catalog.Revision), PeerId, ClusterId, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (ComponentDisposed || !string.Equals(identity, captured, StringComparison.Ordinal))
            {
                return false;
            }
            catalog = result;
            return result is not null;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            problem = "Runtime change could not be confirmed. Refresh before retrying.";
            catalog = null;
            return false;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>版・検出元・利用可否を一行で区別します。</summary>
    /// <returns>選択肢の表示です。</returns>
    private string EntryLabel(ScriptRuntimeEntry entry)
    {
        string status = Ui["script.runtime.source." + entry.Source];
        if (!entry.Available)
        {
            status = Ui["Unavailable"];
        }
        return entry.Path + " · " + entry.Version + " · " + status;
    }
}
