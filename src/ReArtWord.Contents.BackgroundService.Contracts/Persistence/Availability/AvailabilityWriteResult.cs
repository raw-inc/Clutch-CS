using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>候補の採用を永続保存の成功と区別する結果です。</summary>
/// <param name="PersistenceOutcome">実保存の成否です。揮発採用でもSucceededに変えません。</param>
/// <param name="Applied">現在利用する値として採用したかです。</param>
/// <param name="Current">現在の値と保存状態です。</param>
/// <param name="Reason">未採用や未確定の理由です。</param>
/// <param name="AdoptionOutcome">採用処理自身の結果です。採用不明を未実施へ丸めません。</param>
public sealed record AvailabilityWriteResult(EOutcome PersistenceOutcome, bool Applied, AvailabilityValue? Current, string? Reason = null, EOutcome? AdoptionOutcome = null);
