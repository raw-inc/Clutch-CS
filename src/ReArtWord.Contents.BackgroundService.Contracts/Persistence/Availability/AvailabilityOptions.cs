namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>用途別方針と候補・確認済みキャッシュの有限上限です。</summary>
/// <param name="Purposes">未指定用途は旧確認済み維持・オフライン読取り専用です。</param>
/// <param name="MaximumItems">同時保持する用途項目の上限です。</param>
/// <param name="MaximumBytes">確認済み値と候補値を合わせた保持バイト上限です。</param>
public sealed record AvailabilityOptions(IReadOnlyDictionary<EStoragePurpose, AvailabilityPolicy>? Purposes = null, int MaximumItems = 256, int MaximumBytes = 1048576);
