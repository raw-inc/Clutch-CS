using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>通常値と保存状態を混同しない読取り結果です。</summary>
/// <param name="Value">取得不能ではnull、確認済み未存在ではStateと併せて判断します。</param>
/// <param name="Revision">この値の版です。</param>
/// <param name="State">保存・現在有効・復旧の区別です。</param>
public sealed record AvailabilityValue(NodeValue? Value, string? Revision, AvailabilityState State);
