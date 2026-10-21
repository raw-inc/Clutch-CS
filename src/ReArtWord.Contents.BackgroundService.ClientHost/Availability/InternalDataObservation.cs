namespace ReArtWord.Contents.BackgroundService.ClientHost.Availability;

/// <summary>本文を複製せず観測時刻と未確認書込みだけを保持します。</summary>
internal sealed record InternalDataObservation(DateTimeOffset? ConfirmedAt, Guid? PendingCommandId = null, Guid? OperationId = null);
