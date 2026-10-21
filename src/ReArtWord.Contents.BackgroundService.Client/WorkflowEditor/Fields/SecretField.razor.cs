using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>秘密の値をワークフローへ含めず、その場で登録と選択を行います。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class SecretField
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

    /// <summary>編集中ノード。</summary>
    [Parameter]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>秘密値ではなく登録名。</summary>
    [Parameter]
    public string Value
    {
        get; set;
    } = string.Empty;

    /// <summary>接続または保護による編集禁止。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>登録名だけを設定へ保存します。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get; set;
    }

    private bool open;
    private bool busy;
    private string name = string.Empty;
    private string secret = string.Empty;
    private string message = string.Empty;
    private readonly List<string> names = [];
    private string? cluster;
    private string? peer;
    private int generation;
    private bool CanEdit => !Disabled && generation == Model.Generation && cluster is not null && peer is not null
        && string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal) && !Model.Locks.Node(Model.Draft.Document.Id, NodeId);

    /// <summary>選択したクラスタの登録名だけを取得します。</summary>
    /// <returns>秘密値を含まない候補の更新。</returns>
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cluster = Api.SelectedClusterId;
        peer = Api.SelectedPeerId;
        generation = Model.Generation;
        name = Value;
        secret = string.Empty;
        names.Clear();
        message = string.Empty;
        open = true;
        busy = true;
        try
        {
            if (CanEdit)
            {
                names.AddRange(await Api.GetForPeerAsync<string[]>("/api/secrets", peer, cluster, cancellationToken).ConfigureAwait(true) ?? []);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = "Saved key names could not be read. Check the cluster connection.";
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>画面を閉じると秘密入力を消去します。</summary>
    private void Close()
    {
        secret = string.Empty;
        open = false;
    }

    /// <summary>登録名だけを通常の設定変更へ渡します。</summary>
    /// <returns>選択結果の通知。</returns>
    private async Task SelectAsync(CancellationToken cancellationToken = default)
    {
        if (CanEdit)
        {
            await ValueChanged.InvokeAsync(name).ConfigureAwait(true);
            Close();
        }
    }

    /// <summary>明示操作を一度だけ送信し、不明な保存結果を自動再実行しません。</summary>
    /// <returns>保存が確認できた登録名の選択。</returns>
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEdit || busy || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(secret))
        {
            return;
        }
        busy = true;
        try
        {
            JsonElement reply = await Api.PutForPeerAsync<JsonElement>("/api/secrets", new SecretChangeRequest(name, secret), peer, cluster, cancellationToken).ConfigureAwait(true);
            string saved = "saved";
            if (reply.TryGetProperty(saved, out JsonElement confirmed) && confirmed.ValueKind == JsonValueKind.True)
            {
                await SelectAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                message = "Key storage was not confirmed. Check the original operation before trying again.";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = "Key storage was not confirmed. Check the original operation before trying again.";
        }
        finally
        {
            secret = string.Empty;
            busy = false;
        }
    }
}
