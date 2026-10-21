namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>処理中転送の識別子です。</summary>
/// <param name="Id">Idの指定値。</param>
public sealed record FileStageToken(Guid Id);
