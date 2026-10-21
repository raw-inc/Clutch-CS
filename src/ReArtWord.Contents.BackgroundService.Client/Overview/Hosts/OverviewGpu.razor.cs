using System.Globalization;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;

/// <summary>GPU使用率と専用・共有メモリの実測を表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class OverviewGpu
{
    /// <summary>表示対象となるGPUの実測項目です。</summary>
    [Parameter, EditorRequired]
    public ResourceMetric Gpu
    {
        get;
        set;
    } = null!;

    /// <summary>GPUに対応するメモリ実測を含む観測値です。</summary>
    [Parameter]
    public ResourceSample? Sample
    {
        get;
        set;
    }

    /// <summary>対象となる有限の実測値を取得不能と区別して返します。</summary>
    /// <returns>GPUの対象指標の有限値、取得不能ならnullです。</returns>
    private double? Value(string name) => DashboardMetrics.Value(Sample, name, Gpu.Target);

    /// <summary>表示できる有限の実測値かを判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private static bool Valid(double? value) => value is double number && double.IsFinite(number) && 0 <= number;

    /// <summary>取得不能を区別してメモリ量を二進単位で表示します。</summary>
    /// <returns>GiBまたはMiBの使用量、または未計測の表示です。</returns>
    private string Bytes(double? value) => !Valid(value) ? Ui["Not measured"]
                                                         : 1073741824 <= value ? (value.Value / 1073741824).ToString("0.00", CultureInfo.CurrentCulture) + " GiB"
                                                                               : (value!.Value / 1048576).ToString("0.0", CultureInfo.CurrentCulture) + " MiB";

    /// <summary>有効な使用量と上限からメモリ使用率を求めます。</summary>
    /// <returns>小数点以下1桁の使用率と単位、有効な上限がなければダッシュです。</returns>
    private string MemoryPercent(string usedMetric, string totalMetric)
    {
        double? used = Value(usedMetric), total = Value(totalMetric);
        return Valid(used) && Valid(total) && 0 < total ? (100 * used!.Value / total!.Value).ToString("0.0", CultureInfo.CurrentCulture) + " %"
                                                        : "—";
    }
}
