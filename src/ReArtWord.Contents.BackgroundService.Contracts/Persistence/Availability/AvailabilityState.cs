namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

/// <summary>値や秘密を含めず全クライアントへ示す保存・稼働・復旧状態です。</summary>
/// <param name="Purpose">事前方針の用途です。</param>
/// <param name="DatasetId">内部データまたは設定の対象です。</param>
/// <param name="PeerId">現在値を所有するPeerです。</param>
/// <param name="SavedRevision">最後の確認済みprimary保存版です。</param>
/// <param name="EffectiveRevision">現在利用している版です。</param>
/// <param name="RecoveryRevision">再起動時に永続状態から採用できる版です。</param>
/// <param name="IsUnsaved">現在候補を永続保存していません。</param>
/// <param name="IsLocalUnconfirmed">まだ共有へ統合していないローカル変更です。</param>
/// <param name="IsStale">最後の観測後に変化した可能性があります。</param>
/// <param name="CanWrite">現在の方針で内部候補を変更できるかです。</param>
/// <param name="Reason">状態の安定した理由です。</param>
/// <param name="FallbackBackend">確認して切り替えた明示候補保存先です。</param>
/// <param name="CandidateRevision">有効化前を含む保持候補の版です。</param>
/// <param name="CandidateIsUnsaved">未採用候補も含め、候補を永続保存していないかです。</param>
/// <param name="CanRead">確認不能な値を正常なnullへ変えず、現在値を利用できるかです。</param>
public sealed record AvailabilityState(EStoragePurpose Purpose, string DatasetId, string PeerId, string? SavedRevision, string? EffectiveRevision, string? RecoveryRevision, bool IsUnsaved = false, bool IsLocalUnconfirmed = false, bool IsStale = false, bool CanWrite = true, string? Reason = null, EStorageBackend? FallbackBackend = null, string? CandidateRevision = null, bool CandidateIsUnsaved = false, bool CanRead = true);
