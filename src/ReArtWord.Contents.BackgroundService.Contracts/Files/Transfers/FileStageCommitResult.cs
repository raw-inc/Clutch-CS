namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>転送した内容の最終確定結果です。</summary>
/// <param name="DestinationPath">DestinationPathの指定値。</param>
/// <param name="ManifestSha256">ManifestSha256の指定値。</param>
/// <param name="Bytes">Bytesの指定値。</param>
/// <param name="Skipped">内容を確定せず、既存先を保持した結果です。</param>
/// <param name="SkipReason">既存またはロックによるスキップ理由です。</param>
public sealed record FileStageCommitResult(string DestinationPath, string ManifestSha256, long Bytes, bool Skipped = false, string? SkipReason = null);
