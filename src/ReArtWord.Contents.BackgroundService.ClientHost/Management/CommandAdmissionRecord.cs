using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>再送時に初回の受付期限を復元するための記録です。操作本文は保存せず、同一性の検証に使うハッシュと期限の準備状態を保持します。</summary>
internal sealed record CommandAdmissionRecord(string Fingerprint, bool Prepared, RequestClockStamp? Timing);
