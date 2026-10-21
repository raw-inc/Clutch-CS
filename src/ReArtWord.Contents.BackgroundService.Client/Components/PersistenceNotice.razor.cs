using System.Globalization;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>観測したピアの未保存・未統合状態を通知します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class PersistenceNotice
{
    /// <summary>この表示範囲で観測したピアの一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<PeerInfo> Peers
    {
        get;
        set;
    } = [];

    /// <summary>接続を確認できているかどうかです。</summary>
    [Parameter]
    public bool Connected
    {
        get;
        set;
    }

    private bool open;
    private bool HasProblems => Peers.Any(peer => peer.PersistenceStatus is { } status && (0 < status.UnsavedActive || 0 < status.UnsavedCandidates || 0 < status.LocalUnconfirmed));

    /// <summary>取得不能を区別した件数表示を作ります。</summary>
    /// <returns>件数の数値文字列、取得できない場合は未確認を示す表示です。</returns>
    private string Count(int? count) => count?.ToString(CultureInfo.InvariantCulture) ?? Ui["Unconfirmed"];
}
