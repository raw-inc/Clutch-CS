using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>表示中のノードの履歴だけを照会し、媒体を自動取得せず最大50行にまとめます。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class NodePreviewHistory
{
    [Inject]
    private EditorPreviews Previews
    {
        get; set;
    } = null!;

    /// <summary>実行元のクラスタです。</summary>
    [Parameter]
    public string? ClusterId
    {
        get; set;
    }

    /// <summary>現在の表示対象で、実際にプレビューを保持するPeerの最新取得です。</summary>
    // 抑制理由: 親の描画ごとに不変の観測一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "描画ごとに不変の観測一覧を受け取ります。")]
    public IReadOnlyList<NodePreviewSnapshot> Snapshots
    {
        get; set;
    } = [];

    private readonly List<NodePreviewHistoryEntry> rows = [];
    private string? identity;
    private bool loading, unavailable;
    private int generation;

    /// <summary>取得IDが変わったときだけ履歴を読み、遅れた旧表示の応答を破棄します。</summary>
    /// <returns>複数Peerを含む新しい順の履歴です。</returns>
    // 抑制理由: Blazorの固定署名です。画面寿命のトークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。画面寿命のトークンを使用します。")]
    protected override async Task OnParametersSetAsync()
    {
        string key = ClusterId + "/" + string.Join('/', Snapshots.Select(snapshot => snapshot.Id).Order());
        if (string.Equals(identity, key, StringComparison.Ordinal))
        {
            return;
        }
        identity = key;
        int version = ++generation;
        rows.Clear();
        unavailable = false;
        if (ClusterId is not { } cluster || Snapshots.Count == 0)
        {
            loading = false;
            return;
        }
        loading = true;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(UiCancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        List<NodePreviewHistoryEntry> found = [];
        bool failed = false;
        foreach (NodePreviewSnapshot[] batch in Snapshots.DistinctBy(snapshot => snapshot.PeerId).Take(256).Chunk(4))
        {
            IReadOnlyList<NodePreviewHistoryEntry>?[] received = await Task.WhenAll(batch.Select(ReadAsync)).ConfigureAwait(true);
            failed |= received.Any(result => result is null);
            found.AddRange(received.OfType<IReadOnlyList<NodePreviewHistoryEntry>>().SelectMany(result => result));
            if (timeout.IsCancellationRequested)
            {
                failed = true;
                break;
            }
        }
        if (version == generation && !ComponentDisposed)
        {
            rows.AddRange(found.DistinctBy(row => row.Snapshot.Id).OrderByDescending(row => row.Snapshot.CapturedAt).ThenByDescending(row => row.Number).Take(50));
            unavailable = failed;
            loading = false;
        }

        async Task<IReadOnlyList<NodePreviewHistoryEntry>?> ReadAsync(NodePreviewSnapshot snapshot)
        {
            try
            {
                return await Previews.ReadHistoryAsync(cluster, snapshot, timeout.Token).ConfigureAwait(true);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
            {
                return null;
            }
        }
    }
}
