using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>秘密本文を持たず、元要求への結び付けと時計換算の完了状態だけを保存します。</summary>
internal sealed record CommandAdmissionRecord(string Fingerprint, bool Prepared, RequestClockStamp? Timing);
