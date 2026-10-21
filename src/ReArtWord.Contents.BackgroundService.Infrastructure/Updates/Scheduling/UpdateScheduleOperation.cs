using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Scheduling;

/// <summary>予約変更要求の指紋と、再実行せず照会する元結果です。</summary>
/// <param name="Fingerprint">元要求の内容です。</param>
/// <param name="Result">nullは開始だけ確認できた操作です。</param>
internal sealed record UpdateScheduleOperation(string Fingerprint, UpdateScheduleResult? Result = null);
