namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>実行対象の補償退避を指す不透明な識別子です。</summary>
/// <param name="Id">Idの指定値。</param>
/// <param name="ExecutionId">ExecutionIdの指定値。</param>
public sealed record FileCompensationToken(Guid Id, Guid ExecutionId);
