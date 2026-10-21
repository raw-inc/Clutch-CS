using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;

/// <summary>補償時まで維持するパス退避と競合リースです。</summary>
/// <param name="Token">識別子。</param>
/// <param name="Directory">内部保存先。</param>
/// <param name="Backups">退避状態。</param>
/// <param name="Lease">競合範囲の所有。</param>
/// <param name="PreparedResult">副作用を開始しないことを確定した結果です。</param>
/// <param name="ConflictPolicy">保持した排他方針です。</param>
internal sealed record FileSnapshot(FileCompensationToken Token, string Directory, IReadOnlyList<FileBackup> Backups, IDisposable Lease, NodeResult? PreparedResult = null, EFileConflictPolicy? ConflictPolicy = null);
