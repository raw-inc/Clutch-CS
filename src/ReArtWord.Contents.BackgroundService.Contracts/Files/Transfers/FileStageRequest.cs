using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>明示再要求時の同一性を確認する転送開始情報です。</summary>
/// <param name="RequestId">RequestIdの指定値。</param>
/// <param name="SourcePeer">SourcePeerの指定値。</param>
/// <param name="SourcePath">SourcePathの指定値。</param>
/// <param name="DestinationPath">DestinationPathの指定値。</param>
/// <param name="ManifestSha256">ManifestSha256の指定値。</param>
/// <param name="IsDirectory">IsDirectoryの指定値。</param>
/// <param name="Overwrite">Overwriteの指定値。</param>
/// <param name="ExecutionId">実行前補償の競合リースと結び付ける一件識別子。</param>
/// <param name="Operational">転送開始時に固定した共通運用版です。</param>
/// <param name="SkipExisting">確定時に同名があれば変更せず終了します。</param>
/// <param name="SkipLocked">保存先の占有を変更前に確認した場合だけスキップします。</param>
public sealed record FileStageRequest(string RequestId, string SourcePeer, string SourcePath, string DestinationPath, string ManifestSha256, bool IsDirectory, bool Overwrite = false, Guid? ExecutionId = null, ClusterOperationalSnapshot? Operational = null, bool SkipExisting = false, bool SkipLocked = false);
