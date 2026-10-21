namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>JSON保存文書と内容検査値です。</summary>
/// <param name="Hash">内容識別子。</param>
/// <param name="Data">実保存内容。</param>
internal sealed record RepositoryEnvelope(string Hash, RepositoryDocument Data);
