using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;

namespace ReArtWord.Contents.BackgroundService.Client.Components.Files;

/// <summary>明示ホストのファイル一覧だけを閲覧し、選んだ絶対パスをフォームへ返します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class HostFilePicker
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>閲覧を開始したクラスタです。暗黙の接続先へフォールバックしません。</summary>
    [Parameter, EditorRequired]
    public string ClusterId
    {
        get; set;
    } = string.Empty;

    /// <summary>所有ホストの明示Peerです。</summary>
    [Parameter, EditorRequired]
    public string PeerId
    {
        get; set;
    } = string.Empty;

    /// <summary>操作対象のホスト・クラスタを表示します。</summary>
    [Parameter]
    public string Owner
    {
        get; set;
    } = string.Empty;

    /// <summary>初回に親ディレクトリを開く入力済みのファイルパスです。</summary>
    [Parameter]
    public string? InitialPath
    {
        get; set;
    }

    /// <summary>閲覧で確認したファイルのパスを返します。</summary>
    [Parameter]
    public EventCallback<string> Picked
    {
        get; set;
    }

    /// <summary>入力を変更せず選択画面を閉じます。</summary>
    [Parameter]
    public EventCallback Close
    {
        get; set;
    }

    private readonly List<FileBrowseEntry> entries = [];
    private FileBrowseEntry? selected;
    private string? directory, parent;
    private string address = string.Empty;
    private int? nextOffset;
    private bool busy;
    private Func<string>? problem;

    /// <summary>最初の一覧を、呼出元が固定した所有Peerから取得します。</summary>
    /// <returns>初回の閲覧結果です。</returns>
    // 抑制理由: Blazorの固定署名です。I/Oへ画面寿命の取消しを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override Task OnInitializedAsync() => LoadAsync(InitialDirectory(InitialPath), 0, UiCancellation);

    /// <summary>ブラウザのOSに依存せず、Windows・UNC・Unixのファイルパスから親を求めます。</summary>
    /// <returns>入力が空または親を持たない場合はドライブ一覧です。</returns>
    internal static string? InitialDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string value = path.Trim();
        int separator = value.LastIndexOfAny(['/', '\\']);
        if (separator < 0)
        {
            return null;
        }
        if (separator == 0 || separator == 2 && value[1] == ':')
        {
            return value[..(separator + 1)];
        }
        return value[..separator];
    }

    /// <summary>FileTransferと同じ読み取り専用APIを有限ページで参照します。</summary>
    /// <returns>選択元と混ざらない一覧の更新です。</returns>
    private async Task LoadAsync(string? path, int offset, CancellationToken cancellationToken = default)
    {
        if (busy || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        problem = null;
        selected = null;
        if (string.IsNullOrWhiteSpace(ClusterId) || string.IsNullOrWhiteSpace(PeerId))
        {
            problem = Ui.Deferred("The application owner could not be confirmed.");
            return;
        }
        busy = true;
        if (offset == 0)
        {
            entries.Clear();
            nextOffset = null;
        }
        try
        {
            FileBrowseResult? result = await Api.PostForPeerAsync<FileBrowseResult>("/api/files/browse", new FileBrowseRequest(path, offset, FromDrives: true), PeerId, ClusterId, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (result is null || result.Problem is not null)
            {
                problem = Ui.Deferred("The folder could not be read. Check its path and the host permissions.");
                return;
            }
            entries.AddRange(result.Entries);
            directory = result.Path;
            address = result.Path ?? string.Empty;
            parent = result.Parent;
            nextOffset = result.NextOffset;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 閉じたダイアログへ遅れて返る一覧は表示しません。
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            problem = Ui.Deferred("The folder could not be read. Check its path and the host permissions.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>ディレクトリは開き、ファイルは明示選択を確定します。</summary>
    /// <returns>次の一覧または入力欄への反映です。</returns>
    private async Task OpenAsync(FileBrowseEntry entry, CancellationToken cancellationToken = default)
    {
        if (busy || !entries.Contains(entry))
        {
            return;
        }
        if (entry.IsDirectory)
        {
            await LoadAsync(entry.Path, 0, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            selected = entry;
            await ChooseAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>キーボードでもディレクトリを開き、ファイルを選択できます。</summary>
    /// <returns>Enter押下の処理です。</returns>
    private Task KeyAsync(FileBrowseEntry entry, KeyboardEventArgs args, CancellationToken cancellationToken = default)
    {
        string enter = "Enter";
        if (string.Equals(args.Key, enter, StringComparison.Ordinal))
        {
            return OpenAsync(entry, cancellationToken);
        }
        return Task.CompletedTask;
    }

    /// <summary>現在の一覧のファイルだけを返し、フォルダーを実行ファイルにしません。</summary>
    /// <returns>入力反映の完了です。</returns>
    private async Task ChooseAsync(CancellationToken cancellationToken = default)
    {
        if (!busy && !cancellationToken.IsCancellationRequested && selected is { IsDirectory: false } entry && entries.Contains(entry))
        {
            await Picked.InvokeAsync(entry.Path).ConfigureAwait(true);
        }
    }
}
