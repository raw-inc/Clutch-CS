using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;

/// <summary>一台内と全Peer補償のための実行前退避を所有します。</summary>
internal sealed class FileSnapshotStore
{
    private readonly ConcurrentDictionary<Guid, FileSnapshot> snapshots = [];
    private readonly string recoveryRoot;
    private readonly FileAccessPolicy policy;
    private readonly FileOperationCoordinator coordinator;

    /// <summary>共通の許可範囲と競合管理を使用します。</summary>
    internal FileSnapshotStore(FileAccessPolicy policy, FileOperationCoordinator coordinator, string? recoveryDirectory = null)
    {
        this.policy = policy;
        this.coordinator = coordinator;
        recoveryRoot = recoveryDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReArtWord.Contents.BackgroundService", "file-recovery");
        if (!Path.IsPathFullyQualified(recoveryRoot))
        {
            throw new ArgumentException("補償保存先は絶対pathで指定してください。", nameof(recoveryDirectory));
        }
        recoveryRoot = Path.GetFullPath(recoveryRoot);
        _ = new FileAccessPolicy(new FileSystemOptions([recoveryRoot]));
    }

    /// <summary>事前capture済みの一件を検索します。</summary>
    /// <returns>保持中の退避です。</returns>
    internal FileSnapshot? Find(Guid executionId)
    {
        return snapshots.Values.FirstOrDefault(snapshot => snapshot.Token.ExecutionId == executionId);
    }

    /// <summary>全変更対象を変更前に退避し、以後の実行へリースを引き継ぎます。</summary>
    /// <returns>退避と競合範囲です。</returns>
    internal async Task<FileSnapshot> CaptureAsync(Guid executionId, FileOperationPlan plan, EFileConflictPolicy conflictPolicy,
        Func<NodeResult?>? inspect = null, CancellationToken cancellationToken = default)
    {
        FileSnapshot? existing = Find(executionId);
        if (existing is not null)
        {
            return existing;
        }
        IDisposable? lease = null;
        bool retained = false;
        Guid id = Guid.NewGuid();
        string directory = StoragePath(id);
        try
        {
            lease = await coordinator.AcquireAsync(plan.Scopes, conflictPolicy, cancellationToken).ConfigureAwait(false);
            NodeResult? preparedResult = inspect?.Invoke();
            if (preparedResult is not null && preparedResult.Outcome != EOutcome.Succeeded)
            {
                throw new IOException(preparedResult.Message);
            }
            foreach (FileAccessScope scope in plan.Scopes)
            {
                string relative = Path.GetRelativePath(scope.Path, directory);
                string parent = "..";
                string outside = parent + Path.DirectorySeparatorChar;
                if (!Path.IsPathRooted(relative) && !string.Equals(relative, parent, StringComparison.Ordinal) && !relative.StartsWith(outside, StringComparison.Ordinal))
                {
                    throw new IOException("操作treeの内側を補償保存先には指定できません。");
                }
            }
            FileAccessPolicy recoveryPolicy = new(new FileSystemOptions([recoveryRoot]));
            recoveryPolicy.ValidatePath(directory);
            Directory.CreateDirectory(directory);
            List<FileBackup> backups = [];
            foreach (FileAccessScope scope in plan.Scopes.Where(scope => scope.Write && preparedResult is null))
            {
                policy.ValidatePath(scope.Path);
                string? stored = null;
                string? hash = null;
                if (File.Exists(scope.Path) || Directory.Exists(scope.Path))
                {
                    stored = Path.Combine(directory, backups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await FileTree.CopyAsync(scope.Path, stored, policy, null, cancellationToken).ConfigureAwait(false);
                    FileAccessPolicy backupPolicy = new(new FileSystemOptions([directory]));
                    hash = FileTransferManifest.ComputeSha256(await FileTree.ManifestAsync(stored, backupPolicy, cancellationToken).ConfigureAwait(false));
                }
                backups.Add(new(scope.Path, stored, hash));
            }
            FileCompensationToken token = new(id, executionId);
            using (FileStream manifest = new(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(manifest, new FileSnapshotManifest(token, backups, conflictPolicy, preparedResult), cancellationToken: cancellationToken).ConfigureAwait(false);
                await manifest.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            FileSnapshot snapshot = new(token, directory, backups, lease, preparedResult, conflictPolicy);
            if (!snapshots.TryAdd(id, snapshot))
            {
                throw new IOException("退避識別子が競合しました。");
            }
            retained = true;
            lease = null;
            return snapshot;
        }
        finally
        {
            using (lease)
            {
                if (!retained)
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
            }
        }
    }

    /// <summary>退避後にスキップを決めた対象も、将来の補償で変更しないことを先に永続化します。</summary>
    /// <returns>既存先へ触れないことが確定した退避です。</returns>
    internal async Task<FileSnapshot> KeepUnchangedAsync(FileSnapshot snapshot, NodeResult result, CancellationToken cancellationToken = default)
    {
        FileSnapshot unchanged = snapshot with { Backups = [], PreparedResult = result };
        string manifestPath = Path.Combine(snapshot.Directory, "manifest.json");
        string temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        FileAccessPolicy recoveryPolicy = new(new FileSystemOptions([recoveryRoot]));
        recoveryPolicy.ValidatePath(manifestPath);
        recoveryPolicy.ValidatePath(temporary);
        try
        {
            using (FileStream manifest = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(manifest, new FileSnapshotManifest(snapshot.Token, [], snapshot.ConflictPolicy, result), cancellationToken: cancellationToken).ConfigureAwait(false);
                await manifest.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, manifestPath, true);
            snapshots[snapshot.Token.Id] = unchanged;
            return unchanged;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>全対象の既存内容と元の不存在を復元します。</summary>
    /// <returns>全対象の復元完了です。</returns>
    internal async Task RestoreAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        FileSnapshot snapshot = await LoadAsync(token, cancellationToken).ConfigureAwait(false);
        FileAccessPolicy backupPolicy = new(new FileSystemOptions([snapshot.Directory]));
        foreach (FileBackup backup in snapshot.Backups)
        {
            policy.ValidatePath(backup.Path);
            if (backup.StoredPath is not null)
            {
                string actual = FileTransferManifest.ComputeSha256(await FileTree.ManifestAsync(backup.StoredPath, backupPolicy, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actual, backup.ManifestSha256, StringComparison.Ordinal))
                {
                    throw new IOException("退避内容の破損を検出しました。復元先を変更しません。");
                }
            }
        }
        foreach (FileBackup backup in snapshot.Backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            policy.ValidatePath(backup.Path);
            if (File.Exists(backup.Path) || Directory.Exists(backup.Path))
            {
                FileTree.Delete(backup.Path, policy, true);
            }
            if (backup.StoredPath is not null)
            {
                await FileTree.CopyAsync(backup.StoredPath, backup.Path, null, policy, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>明示された退避を削除して競合範囲を解放します。</summary>
    /// <returns>解放完了です。</returns>
    internal async Task ReleaseAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshots.ContainsKey(token.Id) && !Directory.Exists(StoragePath(token.Id)))
        {
            return;
        }
        _ = await LoadAsync(token, cancellationToken).ConfigureAwait(false);
        if (snapshots.TryRemove(token.Id, out FileSnapshot? snapshot))
        {
            using (snapshot.Lease)
            {
                Directory.Delete(snapshot.Directory, true);
            }
        }
    }

    /// <summary>保持済みまたは永続マニフェストから退避を取得します。</summary>
    /// <returns>競合範囲を再取得した退避です。</returns>
    private async Task<FileSnapshot> LoadAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        if (snapshots.TryGetValue(token.Id, out FileSnapshot? found))
        {
            if (found.Token != token)
            {
                throw new InvalidOperationException("補償の一件識別子が一致しません。");
            }
            return found;
        }
        string directory = StoragePath(token.Id);
        FileAccessPolicy recoveryPolicy = new(new FileSystemOptions([directory]));
        recoveryPolicy.ValidatePath(Path.Combine(directory, "manifest.json"));
        FileSnapshotManifest manifest;
        using (FileStream input = new(Path.Combine(directory, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            manifest = await JsonSerializer.DeserializeAsync<FileSnapshotManifest>(input, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new IOException("退避manifestがありません。");
        }
        if (manifest.Token != token)
        {
            throw new IOException("退避manifestの識別子が一致しません。");
        }
        List<FileAccessScope> scopes = [];
        foreach (FileBackup backup in manifest.Backups)
        {
            scopes.Add(new(policy.ResolvePath(backup.Path), true));
        }
        IDisposable? lease = null;
        try
        {
            lease = await coordinator.AcquireAsync(scopes, manifest.ConflictPolicy, cancellationToken).ConfigureAwait(false);
            FileSnapshot snapshot = new(token, directory, manifest.Backups, lease, manifest.PreparedResult, manifest.ConflictPolicy);
            snapshots[token.Id] = snapshot;
            lease = null;
            return snapshot;
        }
        finally
        {
            using (lease)
            {
            }
        }
    }

    /// <summary>OSの一時掃除対象と分けて補償退避を保存します。</summary>
    /// <returns>一つの退避だけに割り当てるパスです。</returns>
    private string StoragePath(Guid id)
    {
        return Path.Combine(recoveryRoot, id.ToString("N"));
    }
}
