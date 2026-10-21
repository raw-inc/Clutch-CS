using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>転送片を保持し、明示再要求と全内容検証後だけ宛先へ確定します。</summary>
internal sealed class TransferStageStore
{
    private readonly ConcurrentDictionary<Guid, FileStageState> states = [];
    private readonly FileAccessPolicy policy;
    private readonly FileOperationCoordinator coordinator;
    private readonly FileSnapshotStore snapshots;
    private readonly EFileConflictPolicy defaultPolicy;
    private readonly EFileInternalFailurePolicy defaultFailurePolicy;

    /// <summary>ローカル操作と許可範囲・競合を共有します。</summary>
    internal TransferStageStore(FileAccessPolicy policy, FileOperationCoordinator coordinator, FileSnapshotStore snapshots, EFileConflictPolicy defaultPolicy, EFileInternalFailurePolicy defaultFailurePolicy)
    {
        this.policy = policy;
        this.coordinator = coordinator;
        this.snapshots = snapshots;
        this.defaultPolicy = defaultPolicy;
        this.defaultFailurePolicy = defaultFailurePolicy;
    }

    /// <summary>同じrequest/source/destination/contentだけを再利用します。</summary>
    /// <returns>新規または照合済み段階です。</returns>
    internal async Task<FileStageToken> OpenAsync(FileStageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        string destination = policy.ResolvePath(request.DestinationPath);
        request = request with { DestinationPath = destination };
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("宛先親がありません。");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("宛先親を先に作成してください。");
        }
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', request.RequestId, request.SourcePeer, request.SourcePath, destination))));
        string directory = policy.ResolvePath(Path.Combine(parent, ".contents-transfer-" + identity));
        string statePath = Path.Combine(directory, "state.json");
        EFileConflictPolicy openingPolicy = request.Operational?.Settings.FileConflictPolicy ?? defaultPolicy;
        if (File.Exists(statePath))
        {
            policy.ValidatePath(statePath);
            using FileStream previous = new(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            FileStageState known = await JsonSerializer.DeserializeAsync<FileStageState>(previous, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new IOException("stage状態が不正です。");
            RequireSamePolicy(known, request);
            openingPolicy = known.ConflictPolicy ?? defaultPolicy;
        }
        using IDisposable lease = await coordinator.AcquireAsync([new(directory, true)], openingPolicy, cancellationToken).ConfigureAwait(false);
        policy.ValidatePath(directory);
        FileStageState state;
        if (Directory.Exists(directory))
        {
            FileTree.ValidateTree(directory, policy);
            using FileStream input = new(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            state = await JsonSerializer.DeserializeAsync<FileStageState>(input, cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new IOException("stage状態がありません。");
            RequireSamePolicy(state, request);
            if (!string.Equals(state.Directory, directory, StringComparison.Ordinal) || state.Request with { ExecutionId = null, Operational = null } != request with { ExecutionId = null, Operational = null })
            {
                throw new IOException("同じrequestIdのsource/destination/contentが一致しません。");
            }
            StringComparer comparer = StringComparer.Ordinal;
            if (OperatingSystem.IsWindows())
            {
                comparer = StringComparer.OrdinalIgnoreCase;
            }
            state = state with { Request = request, Entries = new(state.Entries, comparer), ConflictPolicy = state.ConflictPolicy ?? defaultPolicy, InternalFailurePolicy = state.InternalFailurePolicy ?? defaultFailurePolicy };
        }
        else
        {
            Directory.CreateDirectory(directory);
            StringComparer comparer = StringComparer.Ordinal;
            if (OperatingSystem.IsWindows())
            {
                comparer = StringComparer.OrdinalIgnoreCase;
            }
            state = new(new(Guid.NewGuid()), request, directory, new(comparer), ConflictPolicy: request.Operational?.Settings.FileConflictPolicy ?? defaultPolicy, InternalFailurePolicy: request.Operational?.Settings.FileInternalFailurePolicy ?? defaultFailurePolicy);
            if (request.IsDirectory)
            {
                Directory.CreateDirectory(Path.Combine(directory, "content"));
                state.Entries.Add(string.Empty, new(string.Empty, true, 0, string.Empty));
            }
            await PersistAsync(state, cancellationToken).ConfigureAwait(false);
        }
        states[state.Token.Id] = state;
        return state.Token;
    }

    /// <summary>相対パスを照合し段階ディレクトリを作成します。</summary>
    /// <returns>作成完了です。</returns>
    internal async Task DirectoryAsync(FileStageDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        FileStageState state = Get(request.Stage);
        using IDisposable lease = await coordinator.AcquireAsync([new(state.Directory, true)], state.ConflictPolicy, cancellationToken).ConfigureAwait(false);
        string path = EntryPath(state, request.RelativePath);
        FileManifestEntry entry = new(request.RelativePath, true, 0, string.Empty);
        state.Register(entry);
        if (!state.Committed)
        {
            Directory.CreateDirectory(path);
            await PersistAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>分割データの同一性を確認して途中状態へ書き込みます。</summary>
    /// <returns>保存完了です。</returns>
    internal async Task WriteAsync(FileChunkWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (FileTransferLimits.StreamBytes < request.Data.Length || request.Offset < 0 || request.ExpectedLength < request.Offset || request.ExpectedLength - request.Offset < request.Data.Length
            || !string.Equals(Convert.ToHexString(SHA256.HashData(request.Data.Span)), request.ChunkSha256, StringComparison.Ordinal))
        {
            throw new IOException("転送chunkの範囲またはdigestが一致しません。");
        }
        FileStageState state = Get(request.Stage);
        using IDisposable lease = await coordinator.AcquireAsync([new(state.Directory, true)], state.ConflictPolicy, cancellationToken).ConfigureAwait(false);
        string path = EntryPath(state, request.RelativePath);
        bool registered = state.Entries.ContainsKey(request.RelativePath);
        state.Register(new(request.RelativePath, false, request.ExpectedLength, request.ExpectedSha256));
        if (state.Committed)
        {
            return;
        }
        if (!registered)
        {
            bool saved = false;
            try
            {
                await PersistAsync(state, cancellationToken).ConfigureAwait(false);
                saved = true;
            }
            finally
            {
                // 保存失敗後の再要求でも、未保存のentryを保存済みと誤認しません。
                if (!saved)
                {
                    state.Entries.Remove(request.RelativePath);
                }
            }
        }
        policy.ValidatePath(path);
        using FileStream output = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        policy.ValidatePath(path);
        if (output.Length < request.Offset)
        {
            throw new IOException("未完了の前方chunkを先に送信してください。");
        }
        int overlap = (int)Math.Min(request.Data.Length, output.Length - request.Offset);
        if (0 < overlap)
        {
            byte[] previous = new byte[overlap];
            output.Position = request.Offset;
            await output.ReadExactlyAsync(previous, cancellationToken).ConfigureAwait(false);
            if (!previous.AsSpan().SequenceEqual(request.Data.Span[..overlap]))
            {
                throw new IOException("既存chunkと再要求の内容が一致しません。");
            }
        }
        output.Position = request.Offset + overlap;
        await output.WriteAsync(request.Data[overlap..], cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全マニフェストと内容を検証して宛先を確定します。</summary>
    /// <returns>確定した転送結果です。</returns>
    internal async Task<FileStageCommitResult> CommitAsync(FileStageToken token, CancellationToken cancellationToken = default)
    {
        FileStageState state = Get(token);
        using IDisposable lease = await AcquireCommitAsync(state, cancellationToken).ConfigureAwait(false);
        string destination = policy.ResolvePath(state.Request.DestinationPath);
        string content = Path.Combine(state.Directory, "content");
        string verifyRoot = content;
        if (!File.Exists(content) && !Directory.Exists(content) && (File.Exists(destination) || Directory.Exists(destination)))
        {
            state = state with { Committed = true };
        }
        if (state.Committed)
        {
            verifyRoot = destination;
        }
        long bytes = await VerifyAsync(state, verifyRoot, cancellationToken).ConfigureAwait(false);
        if (state.Committed)
        {
            return new(destination, state.Request.ManifestSha256, bytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        string prior = Path.Combine(state.Directory, "previous");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            if (Directory.Exists(destination) != state.Request.IsDirectory)
            {
                throw new IOException("宛先のfile/directory種類が一致しません。");
            }
            if (state.Request.SkipExisting)
            {
                return new(destination, string.Empty, 0, true, "existing");
            }
            if (!state.Request.Overwrite)
            {
                throw new IOException("宛先同名が存在します。overwrite明示が必要です。");
            }
            try
            {
                FileOutputPolicy.Probe(destination, policy);
                FileTree.ValidateTree(destination, policy);
                policy.ValidatePath(prior);
                policy.ValidatePath(destination);
                Move(destination, prior);
            }
            catch (IOException error) when (state.Request.SkipLocked && FileOutputPolicy.IsLock(error))
            {
                return new(destination, string.Empty, 0, true, "locked");
            }
        }
        policy.ValidatePath(destination);
        policy.ValidatePath(content);
        Move(content, destination);
        state = state with { Committed = true };
        states[token.Id] = state;
        await PersistAsync(state, CancellationToken.None).ConfigureAwait(false);
        if (File.Exists(prior) || Directory.Exists(prior))
        {
            FileTree.Delete(prior, policy, true);
        }
        return new(destination, state.Request.ManifestSha256, bytes);
    }

    /// <summary>明示要求された段階だけを破棄します。</summary>
    /// <returns>破棄完了です。</returns>
    internal async Task DiscardAsync(FileStageToken token, CancellationToken cancellationToken = default)
    {
        FileStageState state = Get(token);
        using IDisposable lease = await coordinator.AcquireAsync([new(state.Directory, true)], state.ConflictPolicy, cancellationToken).ConfigureAwait(false);
        FileTree.Delete(state.Directory, policy, true);
        states.TryRemove(token.Id, out _);
    }

    /// <summary>受信entry全体のsize/hashと想定マニフェストを確認します。</summary>
    /// <returns>検証した総バイト数です。</returns>
    private async Task<long> VerifyAsync(FileStageState state, string content, CancellationToken cancellationToken = default)
    {
        string hash = FileTransferManifest.ComputeSha256(state.Entries.Values.ToArray());
        if (!string.Equals(hash, state.Request.ManifestSha256, StringComparison.Ordinal))
        {
            throw new IOException("転送manifestが一致しません。");
        }
        long bytes = 0;
        foreach (FileManifestEntry entry in state.Entries.Values)
        {
            string path = Path.Combine(content, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            FileEntryInfo actual = await FileTree.StatAsync(path, policy, cancellationToken).ConfigureAwait(false);
            if (actual.IsDirectory != entry.IsDirectory || actual.Length != entry.Length || !string.Equals(actual.Sha256, entry.Sha256, StringComparison.Ordinal))
            {
                throw new IOException("転送内容のsize/hashが一致しません。");
            }
            bytes += actual.Length;
        }
        FileTree.ValidateTree(content, policy);
        if (Directory.Exists(content))
        {
            string[] actualPaths = Directory.GetFileSystemEntries(content, "*", SearchOption.AllDirectories);
            if (actualPaths.Length + 1 != state.Entries.Count)
            {
                throw new IOException("stage内に未指定のentryがあります。");
            }
            foreach (string actualPath in actualPaths)
            {
                string relative = Path.GetRelativePath(content, actualPath).Replace(Path.DirectorySeparatorChar, '/');
                if (!state.Entries.ContainsKey(relative))
                {
                    throw new IOException("stageに未指定のpathがあります。");
                }
            }
        }
        return bytes;
    }

    /// <summary>補償で確保済みの宛先リースを重複取得しません。</summary>
    /// <returns>コミット一件のリースです。</returns>
    private async Task<IDisposable> AcquireCommitAsync(FileStageState state, CancellationToken cancellationToken = default)
    {
        List<FileAccessScope> scopes = [new(state.Directory, true)];
        FileSnapshot? snapshot = null;
        if (state.Request.ExecutionId is Guid executionId)
        {
            snapshot = snapshots.Find(executionId);
        }
        if (snapshot is null || !snapshot.Backups.Any(backup => string.Equals(backup.Path, state.Request.DestinationPath, StringComparison.Ordinal)))
        {
            scopes.Add(new(state.Request.DestinationPath, true));
        }
        return await coordinator.AcquireAsync(scopes, state.ConflictPolicy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>段階識別子を解決します。</summary>
    /// <returns>保持する状態です。</returns>
    private FileStageState Get(FileStageToken token)
    {
        return states.GetValueOrDefault(token.Id) ?? throw new IOException("stageを先に開いてください。");
    }

    /// <summary>保持段階の二方針と一致しない新要求を転送前に拒否します。</summary>
    private void RequireSamePolicy(FileStageState state, FileStageRequest request)
    {
        EFileConflictPolicy conflict = request.Operational?.Settings.FileConflictPolicy ?? defaultPolicy;
        EFileInternalFailurePolicy failure = request.Operational?.Settings.FileInternalFailurePolicy ?? defaultFailurePolicy;
        if ((state.ConflictPolicy ?? defaultPolicy) != conflict || (state.InternalFailurePolicy ?? defaultFailurePolicy) != failure)
        {
            throw new FileStagePolicyMismatchException();
        }
    }

    /// <summary>相対パスの越境を拒否します。</summary>
    /// <returns>段階内の絶対パスです。</returns>
    private string EntryPath(FileStageState state, string relative)
    {
        string[] parts = relative.Split(['/', '\\']);
        string parent = "..";
        string current = ".";
        if (Path.IsPathRooted(relative) || parts.Any(part => string.Equals(part, parent, StringComparison.Ordinal) || string.Equals(part, current, StringComparison.Ordinal)) || relative.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("stageの相対pathが不正です。");
        }
        return policy.ResolvePath(Path.Combine(state.Directory, "content", relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>実体の種類を維持して同じvolume内で移動します。</summary>
    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    /// <summary>再要求に必要なマニフェストをflushして保存します。</summary>
    /// <returns>保存完了です。</returns>
    private async Task PersistAsync(FileStageState state, CancellationToken cancellationToken = default)
    {
        string temporary = policy.ResolvePath(Path.Combine(state.Directory, "state.next"));
        using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, state, cancellationToken: cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        string destination = policy.ResolvePath(Path.Combine(state.Directory, "state.json"));
        File.Move(temporary, destination, true);
    }
}
