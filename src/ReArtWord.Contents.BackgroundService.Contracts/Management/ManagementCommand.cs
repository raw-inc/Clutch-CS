using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>監視処理から選択クラスタの元Peer実体へ送る管理操作です。</summary>
/// <param name="RequestId">再送・照会を通じて固定する外側要求ID。</param>
/// <param name="ClusterId">明示したクラスタ。</param>
/// <param name="TargetPeerId">明示した論理Peer。</param>
/// <param name="TargetInstanceId">新規実行を許す元の実体。</param>
/// <param name="ExpiresAt">未開始受理の期限。開始済み処理の停止期限ではありません。</param>
/// <param name="Operation">登録済み管理操作名。</param>
/// <param name="Payload">ドメイン operationIdを含む元の引数。</param>
public sealed record ManagementCommand(Guid RequestId, string ClusterId, string TargetPeerId, Guid TargetInstanceId, DateTimeOffset ExpiresAt, string Operation, NodeValue? Payload)
{
    /// <summary>新規管理要求の受付期間を発行時に固定します。保存済み操作を復元する場合は使用しません。</summary>
    /// <returns>本文の期限と、時刻補正に依存しない受付予算を持つ要求です。</returns>
    public static ManagementCommand Start(Guid requestId, string clusterId, string peerId, Guid instanceId,
        TimeSpan lifetime, string operation, NodeValue? payload, TimeProvider? timeProvider = null)
    {
        Execution.RequestAdmission admission = Execution.RequestAdmission.Start(timeProvider ?? TimeProvider.System, lifetime);
        return new(requestId, clusterId, peerId, instanceId, admission.ExpiresAt, operation, payload) { Admission = admission };
    }

    /// <summary>受信側の単調時計で判定する受付期限です。要求の同一性を保つため、JSONには含めません。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Execution.RequestAdmission? Admission
    {
        get; init;
    }

    /// <summary>初回送信の準備が保存済みかどうかです。trueかつDeliveryTimingがnullの場合は、旧形式での再送を指定します。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DeliveryPrepared
    {
        get; init;
    }

    /// <summary>初回の送信準備で保存した期限です。ClientHost再起動後も同じ値を使い、再送で受付期間が延びるのを防ぎます。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Execution.RequestClockStamp? DeliveryTiming
    {
        get; init;
    }
}
