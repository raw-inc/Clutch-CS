using ReArtWord.Contents.BackgroundService.Contracts.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>確認済み代替候補と、その候補の採用結果を別に保存します。</summary>
/// <param name="Backend">明示媒体です。</param>
/// <param name="Hash">候補全体のハッシュです。</param>
/// <param name="Active">採用完了を確認した候補かです。</param>
/// <param name="Slot">前回採用候補を上書きしない二つの候補領域です。</param>
internal sealed record AvailabilityFallbackPointer(EStorageBackend Backend, string Hash, bool Active, int Slot = 0);
