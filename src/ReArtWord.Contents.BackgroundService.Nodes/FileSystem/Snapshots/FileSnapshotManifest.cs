using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;

/// <summary>再起動後にも明示補償を照合する退避マニフェストです。</summary>
/// <param name="Token">一件と退避の識別子。</param>
/// <param name="Backups">退避した変更対象。</param>
/// <param name="ConflictPolicy">取得開始時に固定した共通競合方針です。</param>
/// <param name="PreparedResult">退避・補償が不要と確定したスキップ結果です。</param>
internal sealed record FileSnapshotManifest(FileCompensationToken Token, IReadOnlyList<FileBackup> Backups, EFileConflictPolicy? ConflictPolicy = null, NodeResult? PreparedResult = null);
