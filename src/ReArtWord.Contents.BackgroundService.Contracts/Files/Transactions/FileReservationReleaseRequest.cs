namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>元Core停止後のファイルアクセス終了を運用者が確認し、元実行結果を変えずに予約だけを明示解放します。</summary>
public sealed record FileReservationReleaseRequest(Guid Id, Guid ExpectedOwnerInstanceId, bool FileAccessEndedConfirmed);
