using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>実行対象を固定してリモートの許可フォルダーを閲覧し、パスだけを保存します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class PathField
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    [Inject]
    private SettingChoiceProvider Choices
    {
        get; set;
    } = null!;

    /// <summary>編集ノード。</summary>
    [Parameter]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>転送元・宛先などの設定名。</summary>
    [Parameter]
    public string Setting
    {
        get; set;
    } = string.Empty;

    /// <summary>保存パス。</summary>
    [Parameter]
    public string Value
    {
        get; set;
    } = string.Empty;

    /// <summary>ディレクトリだけを選ぶか。</summary>
    [Parameter]
    public bool Directories
    {
        get; set;
    }

    /// <summary>ファイルとディレクトリの両方を選べる転送などに使用します。</summary>
    [Parameter]
    public bool AllowDirectories
    {
        get; set;
    }

    /// <summary>接続・保護による編集禁止。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>パスを設定欄へ反映します。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get; set;
    }

    private bool open;
    private bool busy;
    private string? peer;
    private string? cluster;
    private string? problem;
    private int generation;
    private string newName = string.Empty;
    private FileBrowseResult? result;
    private string? CurrentPeer
    {
        get
        {
            if (Model.Draft.Document.Nodes.FirstOrDefault(node => string.Equals(node.Id, NodeId, StringComparison.Ordinal)) is { } node)
            {
                return ResourceSettingContext.Peer(node, Setting, Model.Draft.Document, Choices);
            }
            return null;
        }
    }

    private bool CanEdit => !Disabled && generation == Model.Generation && string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal)
        && string.Equals(peer, CurrentPeer, StringComparison.Ordinal) && !Model.Locks.Node(Model.Draft.Document.Id, NodeId);

    /// <summary>開いた対象を固定し、他のPCの候補を混ぜません。</summary>
    /// <returns>候補の初回取得です。</returns>
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        peer = CurrentPeer;
        cluster = Api.SelectedClusterId;
        generation = Model.Generation;
        result = null;
        newName = string.Empty;
        open = true;
        await ReadAsync(null, 0, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>一覧取得は読み取り専用の管理操作で実行します。</summary>
    /// <returns>有限ページの画面反映です。</returns>
    private async Task ReadAsync(string? path, int offset, CancellationToken cancellationToken = default)
    {
        if (peer is null || cluster is null || !CanEdit)
        {
            problem = "Select one execution peer to verify these choices.";
            return;
        }
        busy = true;
        problem = null;
        try
        {
            FileBrowseResult? page = await Api.PostForPeerAsync<FileBrowseResult>("/api/files/browse", new FileBrowseRequest(path, offset), peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (CanEdit)
            {
                result = page;
                problem = page?.Problem;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            problem = "Could not browse this peer. Check its connection and allowed file roots.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>対象が変わった古い一覧から確定しません。</summary>
    /// <returns>パス変更の通知です。</returns>
    private async Task ChooseAsync(string path, CancellationToken cancellationToken = default)
    {
        if (CanEdit)
        {
            await ValueChanged.InvokeAsync(path).ConfigureAwait(true);
            open = false;
        }
    }

    /// <summary>リモート側のパス区切りを保ち、未作成の名前を選択できます。</summary>
    /// <returns>新しいパスの通知です。ファイルの作成は行いません。</returns>
    private Task ChooseNewAsync(CancellationToken cancellationToken = default)
    {
        if (result?.Path is not { } parent || string.IsNullOrWhiteSpace(newName) || newName.Contains('/', StringComparison.Ordinal) || newName.Contains('\\', StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }
        string separator = "/";
        if (parent.Contains('\\', StringComparison.Ordinal))
        {
            separator = "\\";
        }
        return ChooseAsync(parent.TrimEnd('/', '\\') + separator + newName, cancellationToken);
    }
}
