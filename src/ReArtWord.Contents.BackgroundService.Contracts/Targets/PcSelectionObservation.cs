namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>PC選択で実際に比較した値と、比較できなかった理由です。</summary>
/// <param name="HostId">対象PC。</param>
/// <param name="PeerId">観測を取得したPeer。</param>
/// <param name="InstanceId">観測したプロセス実体。</param>
/// <param name="Resource">比較したリソース。</param>
/// <param name="Value">比較値。取得できなかった場合はnull。</param>
/// <param name="Unit">比較値の単位。</param>
/// <param name="ObservedAt">測定時刻。</param>
/// <param name="GpuId">比較対象のGPU。</param>
/// <param name="Reason">候補を比較できなかった理由。</param>
public sealed record PcSelectionObservation(string HostId, string PeerId, Guid InstanceId, EPcResource Resource,
    double? Value, string Unit, DateTimeOffset? ObservedAt, string? GpuId = null, string? Reason = null);
