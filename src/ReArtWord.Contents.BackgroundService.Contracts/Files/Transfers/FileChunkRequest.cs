using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>内容版を固定して読む有限分割データです。</summary>
/// <param name="Path">Pathの指定値。</param>
/// <param name="Offset">Offsetの指定値。</param>
/// <param name="Count">Countの指定値。</param>
/// <param name="ExpectedLength">ExpectedLengthの指定値。</param>
/// <param name="ExpectedSha256">ExpectedSha256の指定値。</param>
/// <param name="Operational">元転送の取得開始時に固定した共通版です。</param>
public sealed record FileChunkRequest(string Path, long Offset, int Count, long ExpectedLength, string ExpectedSha256, ClusterOperationalSnapshot? Operational = null);
