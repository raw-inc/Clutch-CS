using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;

/// <summary>固定対象ごとの結果を失わないクラスタ更新状態です。</summary>
/// <param name="Request">一回へ固定された入力です。</param>
/// <param name="Outcome">全体結果です。</param>
/// <param name="Completed">終端状態を確認できたかです。</param>
/// <param name="Peers">各対象の現在結果です。</param>
/// <param name="Reason">全体の安定した理由です。</param>
public sealed record ClusterUpdateStatus(ClusterUpdateRequest Request, EOutcome Outcome, bool Completed, IReadOnlyList<UpdatePeerResult> Peers, string? Reason = null)
{
    /// <summary>元操作と同じ方式で通知・配送IDを作ります。方式のない既存記録は旧IDを維持します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
