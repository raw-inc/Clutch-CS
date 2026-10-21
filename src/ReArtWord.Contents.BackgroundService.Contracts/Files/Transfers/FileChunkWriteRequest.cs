namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>一つの検証可能な転送片を書き込みます。</summary>
/// <param name="Stage">Stageの指定値。</param>
/// <param name="RelativePath">RelativePathの指定値。</param>
/// <param name="Offset">Offsetの指定値。</param>
/// <param name="Data">Dataの指定値。</param>
/// <param name="ChunkSha256">ChunkSha256の指定値。</param>
/// <param name="ExpectedLength">ExpectedLengthの指定値。</param>
/// <param name="ExpectedSha256">ExpectedSha256の指定値。</param>
public sealed record FileChunkWriteRequest(FileStageToken Stage, string RelativePath, long Offset, ReadOnlyMemory<byte> Data, string ChunkSha256, long ExpectedLength, string ExpectedSha256);
