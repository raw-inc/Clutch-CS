namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>offsetとdigestで検証可能な転送片です。</summary>
/// <param name="Offset">Offsetの指定値。</param>
/// <param name="Data">Dataの指定値。</param>
/// <param name="Sha256">Sha256の指定値。</param>
public sealed record FileChunk(long Offset, ReadOnlyMemory<byte> Data, string Sha256);
