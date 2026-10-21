using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Resources;

/// <summary>共通リソース収集の一時点の結果です。</summary>
/// <param name="Id">一件の採取識別子です。</param>
/// <param name="PeerId">採取したPeerです。</param>
/// <param name="ObservedAt">採取時刻です。</param>
/// <param name="Metrics">取得状態付き実測値です。</param>
/// <param name="HistoryStored">このサンプルの診断履歴保存を確認できたかです。</param>
/// <param name="InstanceId">観測したCore実体。旧記録で未確認ならnullです。</param>
/// <param name="SelectedInterfaceId">起動時の指定IPに対応する一意のNIC。確認できなければnullです。</param>
/// <param name="HistoryPending">定期保存までメモリに保持中かです。保存失敗とは区別します。</param>
public sealed record ResourceSample(Guid Id, string PeerId, DateTimeOffset ObservedAt, IReadOnlyList<ResourceMetric> Metrics, bool HistoryStored = false, Guid? InstanceId = null, string? SelectedInterfaceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HistoryPending = false);
