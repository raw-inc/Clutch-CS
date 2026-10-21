using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;

/// <summary>PC healthだけに永続表示を重ね、クラスタ・Peerの発見一覧は変更しません。</summary>
// 抑制理由: Razorの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開宣言に合わせます。")]
public sealed partial class OverviewHosts
{
    [Inject] private ManagementApi Api
    {
        get;
        set;
    } = null!;

    /// <summary>全クラスタはnull、選択クラスタはその識別子です。</summary>
    [Parameter] public string? ClusterId
    {
        get;
        set;
    }

    /// <summary>現在取得した実測系列です。</summary>
    // 抑制理由: BlazorのParameterです。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "BlazorのParameterです。")]
    public IReadOnlyList<OverviewPeer> LivePeers
    {
        get;
        set;
    } = [];

    /// <summary>詳細表示の選択です。</summary>
    [Parameter] public bool Detailed
    {
        get;
        set;
    }

    /// <summary>表示中の画面だけが取得します。</summary>
    [Parameter] public bool Active
    {
        get;
        set;
    } = true;

    private readonly List<DashboardHostPeer> records = [];
    private readonly List<OverviewPeer> displayPeers = [];
    private DateTimeOffset nextRead;
    private string? scope, problem;

    /// <summary>親の3秒更新に合わせて、保存されたPCを取得します。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    // 抑制理由: Blazorの固定署名で所有トークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名で所有トークンを使用します。")]
    protected override async Task OnParametersSetAsync()
    {
        if (Active && (nextRead <= Api.UtcNow || !string.Equals(scope, ClusterId, StringComparison.Ordinal)))
        {
            await ReadAsync(UiCancellation).ConfigureAwait(true);
        }
        Merge();
    }

    /// <summary>削除直後は次の定期更新を待たず反映します。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    // 抑制理由: UI callbackで所有トークンを使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI callbackで所有トークンを使用します。")]
    private async Task ReloadAsync()
    {
        await ReadAsync(UiCancellation).ConfigureAwait(true);
        Merge();
    }

    /// <summary>Host全体の可用性を判断するため、所属情報は全クラスタ分を取得します。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    private async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<DashboardHostPeer> loaded = await Api.GetAsync<List<DashboardHostPeer>>("/client/dashboard/hosts", cancellationToken).ConfigureAwait(true) ?? [];
            records.Clear();
            records.AddRange(loaded);
            problem = null;
            scope = ClusterId;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            problem = "Saved PC display history is unavailable.";
        }
        nextRead = Api.UtcNow.AddSeconds(3);
    }

    /// <summary>現在の系列を優先し、消えたPCには最後の一標本だけを表示します。</summary>
    private void Merge()
    {
        Dictionary<(string, string?, string), OverviewPeer> merged = LivePeers.Where(peer => peer.Peer.Status != Contracts.Cluster.EPeerStatus.Unavailable && Api.UtcNow - peer.Peer.LastSeen <= TimeSpan.FromSeconds(15)).ToDictionary(peer => (peer.Peer.HostId, peer.ClusterId, peer.Peer.PeerId));
        foreach (DashboardHostPeer entry in records.Where(entry => ClusterId is null || string.Equals(entry.ClusterId, ClusterId, StringComparison.Ordinal)))
        {
            (string, string?, string) key = (entry.Peer.HostId, entry.ClusterId, entry.Peer.PeerId);
            merged.TryGetValue(key, out OverviewPeer? live);
            IReadOnlyList<Contracts.Resources.ResourceSample> samples = entry.Sample is null ? []
                                                                                             : [entry.Sample];
            if (live?.Peer.InstanceId == entry.Peer.InstanceId && live.Samples.Count != 0)
            {
                samples = live.Samples;
            }
            string? state = live?.Problem;
            if (!entry.Online)
            {
                state = "Previous / unconfirmed";
            }
            merged[key] = new(entry.Peer, samples, state) { ClusterId = entry.ClusterId, Offline = !entry.Online };
        }
        displayPeers.Clear();
        displayPeers.AddRange(merged.Values);
    }

    /// <summary>オフラインと実測の鮮度を分け、古い値を正常表示に使いません。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    private bool Previous(OverviewPeer peer) => peer.Offline || peer.Problem is not null || DashboardMetrics.IsStale(peer.Latest, Api.UtcNow);
}
