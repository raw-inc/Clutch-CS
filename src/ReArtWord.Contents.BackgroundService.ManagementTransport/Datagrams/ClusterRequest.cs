using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>明示対象へ配送する種類付きの管理要求です。</summary>
/// <param name="Id">再送を通じて一定の操作ID。</param>
/// <param name="Kind">登録済み処理種類。</param>
/// <param name="TargetPeerId">対象Peer。</param>
/// <param name="TargetInstanceId">対象起動実体。</param>
/// <param name="ExpiresAt">新規開始できる期限。</param>
/// <param name="Payload">処理固有の入力。</param>
public sealed record ClusterRequest(Guid Id, string Kind, string TargetPeerId, Guid TargetInstanceId, DateTimeOffset ExpiresAt, NodeValue Payload)
{
    /// <summary>新しい時刻付きIDと受付期限を、一回の時刻取得から作成します。</summary>
    /// <returns>IDの生成日時と受付期間の基準が一致する要求です。</returns>
    public static ClusterRequest Start(string kind, string targetPeerId, Guid targetInstanceId, TimeSpan lifetime,
        NodeValue payload, TimeProvider? timeProvider = null)
    {
        Contracts.Execution.RequestAdmission admission = Contracts.Execution.RequestAdmission.Start(timeProvider ?? TimeProvider.System, lifetime);
        return new(Guid.CreateVersion7(admission.SourceNow), kind, targetPeerId, targetInstanceId, admission.ExpiresAt, payload) { Admission = admission };
    }

    /// <summary>新規要求の本文と受付期間を発行時に固定します。保存済み要求の再送には使いません。</summary>
    /// <returns>時計が補正されても、発行からの経過時間で期限を守る要求です。</returns>
    public static ClusterRequest Start(Guid id, string kind, string targetPeerId, Guid targetInstanceId, TimeSpan lifetime,
        NodeValue payload, TimeProvider? timeProvider = null)
    {
        Contracts.Execution.RequestAdmission admission = Contracts.Execution.RequestAdmission.Start(timeProvider ?? TimeProvider.System, lifetime);
        return new(id, kind, targetPeerId, targetInstanceId, admission.ExpiresAt, payload) { Admission = admission };
    }

    /// <summary>受信側の単調時計で判定する受付期限です。通信処理が設定し、要求のJSONや内容照合用のハッシュには含めません。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Contracts.Execution.RequestAdmission? Admission
    {
        get; init;
    }
}
