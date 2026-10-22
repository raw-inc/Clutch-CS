using System.Globalization;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;

/// <summary>同じPCの計測元を維持し、CPU・メモリー・GPU・ディスク・NICの実測値を表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class OverviewHostCard
{
    [Inject] private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject] private OperationJournal Journal
    {
        get;
        set;
    } = null!;

    /// <summary>同じPCへ接続できる実体を、現在の画面選択とは独立して固定します。</summary>
    // 抑制理由: BlazorのParameterです。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "BlazorのParameterです。")]
    public IReadOnlyList<DashboardHostPeer> PowerPeers
    {
        get;
        set;
    } = [];

    /// <summary>保存表示を削除した後の再取得です。</summary>
    [Parameter] public EventCallback Removed
    {
        get;
        set;
    }

    private static readonly string[] powerActions = ["restart", "shutdown"];
    private bool powerBusy;
    private string? powerProblem;
    private bool Offline => PowerPeers.Count != 0 && PowerPeers.All(peer => !peer.Online);
    private DashboardHostPeer? PowerTarget => PowerPeers.Where(peer => peer.Online && peer.Peer.Status is EPeerStatus.Ready or EPeerStatus.Syncing)
        .OrderByDescending(peer => peer.Peer.InstanceId == item.Peer.InstanceId).ThenBy(peer => peer.ClusterId, StringComparer.Ordinal).FirstOrDefault();
    private string? TargetIdentity => PowerTarget is { } target ? target.ClusterId + "/" + target.Peer.PeerId + "/" + target.Peer.InstanceId
                                                                : null;

    /// <summary>表示確認と同じ実体に一回だけ送信し、結果不明時に再送しません。</summary>
    /// <returns>確認済みの状態、または所有する処理の完了です。</returns>
    // 抑制理由: UI callbackで所有トークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UI callbackで所有トークンを渡します。")]
    private async Task ActAsync(string action)
    {
        if (powerBusy)
        {
            return;
        }
        powerBusy = true;
        powerProblem = null;
        try
        {
            string wake = "wake";
            if (string.Equals(action, wake, StringComparison.Ordinal))
            {
                if (!Offline)
                {
                    return;
                }
                HostWakeResult? wakeResult = await Api.PostAsync<HostWakeResult>("/client/host-manager/wake", new HostWakeStart(Guid.NewGuid(), item.Peer.HostId), UiCancellation).ConfigureAwait(true);
                powerProblem = wakeResult?.Reason ?? "wake.unconfirmed";
                return;
            }
            string remove = "remove";
            if (string.Equals(action, remove, StringComparison.Ordinal))
            {
                if (!Offline)
                {
                    return;
                }
                System.Text.Json.JsonElement removed = await Api.PostAsync<System.Text.Json.JsonElement>("/client/dashboard/hosts/remove", new DashboardHostRemoval(item.Peer.HostId), UiCancellation).ConfigureAwait(true);
                bool deleted = removed.GetProperty("removed").GetBoolean();
                if (deleted)
                {
                    await Removed.InvokeAsync().ConfigureAwait(true);
                }
                else
                {
                    powerProblem = "The PC is online again. Refresh before removing it.";
                }
                return;
            }
            DashboardHostPeer? target = PowerTarget;
            if (target is null || !powerActions.Contains(action, StringComparer.Ordinal))
            {
                powerProblem = "The PC or its state changed. Refresh and confirm the operation again.";
                return;
            }
            EHostPowerAction kind = action switch { "restart" => EHostPowerAction.Restart, _ => EHostPowerAction.Shutdown };
            Guid id = Guid.NewGuid();
            HostPowerRequest request = new(id, item.Peer.HostId, target.Peer.InstanceId, kind);
            Guid command = ManagementCommandIdentity.Create(id, "host.power");
            System.Text.Json.JsonElement response = await Journal.StartAsync(id, Ui.Format("Power action for {0}", item.Peer.HostId), "/api/host/power",
                "/client/commands/" + command + "/result", request, targetPeer: target.Peer.PeerId, targetCluster: target.ClusterId, cancellationToken: UiCancellation).ConfigureAwait(true);
            NodeResult? result = ManagementApi.ReadResponse<NodeResult>(response);
            powerProblem = result?.Outcome == Contracts.Execution.EOutcome.Succeeded ? "The OS accepted the power request. The PC may disconnect before a reply arrives."
                                                                                     : result?.Message ?? "The power request result is unconfirmed. Do not resend it automatically.";
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException or Microsoft.JSInterop.JSException)
        {
            powerProblem = "The power request result is unconfirmed. Do not resend it automatically.";
        }
        finally
        {
            powerBusy = false;
        }
    }

    /// <summary>この表示範囲で観測したピアの一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, EditorRequired, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<OverviewPeer> Peers
    {
        get;
        set;
    } = [];

    /// <summary>取得した実測を過去値として扱う親画面の判定です。</summary>
    [Parameter]
    public Func<OverviewPeer, bool>? IsPrevious
    {
        get;
        set;
    }

    /// <summary>同じPCが所属するクラスタとピアの表示です。</summary>
    [Parameter]
    public string? Memberships
    {
        get;
        set;
    }

    /// <summary>CPU構成、全GPU、NICなどの詳細情報を表示するかどうかです。</summary>
    [Parameter]
    public bool Detailed
    {
        get;
        set;
    }

    private string CpuName => sample?.Metrics.FirstOrDefault(metric => string.Equals(metric.Name, "cpu.name", StringComparison.Ordinal))?.Label ?? Ui["Not measured"];

    private Guid? preferredInstance;
    private OverviewPeer item = null!;
    private ResourceSample? sample;
    private bool stale;
    private double? cpu, memory;
    private ResourceMetric? summaryGpu;
    private readonly List<ResourceMetric> gpus = [];
    private readonly List<OverviewPeer> networkPeers = [];

    /// <summary>計測元を継続して選択し、同じ観測からPCカードとNICの表示値を準備します。</summary>
    protected override void OnParametersSet()
    {
        item = DashboardMetrics.HostPeer(Peers, preferredInstance, Previous);
        preferredInstance = item.Peer.InstanceId;
        sample = item.Latest;
        stale = Previous(item);
        cpu = DashboardMetrics.Value(sample, "cpu.usage");
        memory = DashboardMetrics.Value(sample, "memory.usage");
        summaryGpu = DashboardMetrics.LargestDedicatedGpu(sample);
        gpus.Clear();
        if (sample is not null)
        {
            gpus.AddRange(sample.Metrics.Where(metric => string.Equals(metric.Name, "gpu.usage", StringComparison.Ordinal)));
        }
        networkPeers.Clear();
        networkPeers.AddRange(Peers.GroupBy(peer => peer.Latest?.SelectedInterfaceId ?? peer.Peer.PeerId)
            .Select(group => group.OrderBy(Previous).ThenByDescending(peer => peer.Latest?.ObservedAt).First()));
    }

    /// <summary>同一PCの実測を過去値として扱うべきか確認します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool Previous(OverviewPeer item) => IsPrevious?.Invoke(item) ?? (item.Problem is not null || DashboardMetrics.IsStale(item.Latest, DateTimeOffset.UtcNow));

    /// <summary>未計測を区別した使用率の表示を作ります。</summary>
    /// <returns>小数点以下1桁の使用率、未計測ならダッシュです。</returns>
    private static string Percent(double? value) => value?.ToString("0.0", CultureInfo.CurrentCulture) ?? "—";

    /// <summary>ホスト合計のbytes/sを指定表示単位へ変換し、欠測を区別します。</summary>
    /// <returns>小数点以下2桁の転送量です。</returns>
    internal static string Throughput(double? value, double divisor)
    {
        if (value is double number && double.IsFinite(number) && 0 <= number)
        {
            return (number / divisor).ToString("0.00", CultureInfo.CurrentCulture);
        }
        return "—";
    }

    /// <summary>通信量を十進のMbpsまたはKbpsへ変換し、数値と単位を一緒に選びます。</summary>
    /// <returns>1Mbps未満はKbps、その他はMbpsで表した小数点以下2桁の値です。</returns>
    internal static (string Number, string Unit) NetworkThroughput(double? value)
    {
        if (value is >= 0 and < 125000)
        {
            return (Throughput(value, 125), "Kbps");
        }
        return (Throughput(value, 125000), "Mbps");
    }

    /// <summary>取得不能を区別した件数表示を作ります。</summary>
    /// <returns>件数の数値文字列、取得できない場合は未確認を示す表示です。</returns>
    private static string Count(double? value) => value is > 0 ? value.Value.ToString("0", CultureInfo.CurrentCulture)
                                                               : "—";

    /// <summary>取得したバイト数をGiBの表示へ変換します。</summary>
    /// <returns>小数点以下2桁の数値、未計測ならダッシュです。単位は描画側で添えます。</returns>
    private static string GiB(double? value) => value is >= 0 ? (value.Value / 1073741824).ToString("0.00", CultureInfo.CurrentCulture)
                                                              : "—";

    /// <summary>温度の未計測を区別して摂氏表示を作ります。</summary>
    /// <returns>摂氏温度と単位、または未計測の表示です。</returns>
    private string Temperature(double? value) => value is null ? Ui["Not measured"]
                                                               : value.Value.ToString("0.0", CultureInfo.CurrentCulture) + " °C";

    /// <summary>取得不能を区別して通信量を毎秒の単位で表示します。</summary>
    /// <returns>MiB/sまたはKiB/sの通信量、取得不能ならダッシュです。</returns>
    private static string Rate(double? value) => value is null ? "—"
                                                               : 1048576 <= value.Value ? (value.Value / 1048576).ToString("0.00", CultureInfo.CurrentCulture) + " MiB/s"
                                                                                        : (value.Value / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " KiB/s";
}
