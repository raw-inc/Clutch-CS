using System.Collections.Concurrent;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>Peer RPCとローカルノードで共有する許可範囲・分割転送サービスです。</summary>
public sealed class FileTransferService
{
    private readonly TransferStageStore stages;
    private readonly ConcurrentDictionary<string, (long Length, long WriteTicks, string Hash)> versions = new(StringComparer.Ordinal);

    /// <summary>一台の全ファイル能力で同じ競合管理を使用します。</summary>
    public FileTransferService(FileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        AccessPolicy = new(options);
        Operations = new(options.ConflictPolicy);
        Snapshots = new(AccessPolicy, Operations, options.RecoveryDirectory);
        stages = new(AccessPolicy, Operations, Snapshots, options.ConflictPolicy, options.InternalFailurePolicy);
    }

    /// <summary>このサービスが使用するクラスタ共通設定です。</summary>
    public FileSystemOptions Options
    {
        get;
    }

    /// <summary>ファイル利用能力が共用する許可範囲検証です。</summary>
    public FileAccessPolicy AccessPolicy
    {
        get;
    }

    /// <summary>同じPeerのファイル・media操作が共用できる競合管理です。</summary>
    public FileOperationCoordinator Operations
    {
        get;
    }

    /// <summary>ノード実行と全対象補償が共有する退避です。</summary>
    internal FileSnapshotStore Snapshots
    {
        get;
    }

    /// <summary>sourceのsizeとdigestを取得します。</summary>
    /// <returns>固定した内容の同一性です。</returns>
    public Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken = default) => StatAsync(path, null, cancellationToken);

    /// <summary>一件に固定した共通競合方針で内容版を確認します。</summary>
    /// <returns>実際の内容版です。</returns>
    public async Task<FileEntryInfo> StatAsync(string path, EFileConflictPolicy? conflictPolicy, CancellationToken cancellationToken = default)
    {
        string resolved = AccessPolicy.ResolvePath(path, true);
        using IDisposable lease = await Operations.AcquireAsync([new(resolved, false)], conflictPolicy, cancellationToken).ConfigureAwait(false);
        long before = File.GetLastWriteTimeUtc(resolved).Ticks;
        FileEntryInfo info = await FileTree.StatAsync(resolved, AccessPolicy, cancellationToken).ConfigureAwait(false);
        if (!info.IsDirectory)
        {
            long after = File.GetLastWriteTimeUtc(resolved).Ticks;
            if (before != after)
            {
                throw new IOException("sourceが内容確認中に変更されました。");
            }
            versions[resolved] = (info.Length, after, info.Sha256);
        }
        return info;
    }

    /// <summary>一階層の一覧を最大16件ずつ取得します。</summary>
    /// <returns>件数を制限したページと次の開始位置です。</returns>
    public Task<FileListPage> ListAsync(string path, string? continuation = null, CancellationToken cancellationToken = default) => ListAsync(path, continuation, null, cancellationToken);

    /// <summary>同じ共通方針でページと各項目を読みます。</summary>
    /// <returns>件数を制限したページです。</returns>
    public async Task<FileListPage> ListAsync(string path, string? continuation, EFileConflictPolicy? conflictPolicy, CancellationToken cancellationToken = default)
    {
        string resolved = AccessPolicy.ResolvePath(path, true);
        using IDisposable lease = await Operations.AcquireAsync([new(resolved, false)], conflictPolicy, cancellationToken).ConfigureAwait(false);
        string[] entries = Directory.EnumerateFileSystemEntries(resolved).Order(StringComparer.Ordinal)
            .Where(entry => continuation is null || 0 < string.CompareOrdinal(entry, continuation)).Take(17).ToArray();
        List<FileEntryInfo> result = [];
        foreach (string entry in entries.Take(16))
        {
            result.Add(await StatAsync(entry, conflictPolicy, cancellationToken).ConfigureAwait(false));
        }
        string? next = null;
        if (16 < entries.Length)
        {
            next = entries[15];
        }
        return new(result, next);
    }

    /// <summary>確認済み内容版から最大1MiBの分割データを読みます。</summary>
    /// <returns>offsetとdigestを付けた分割データです。</returns>
    public async Task<FileChunk> ReadChunkAsync(FileChunkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Count < 1 || FileTransferLimits.StreamBytes < request.Count || request.Offset < 0 || request.ExpectedLength < request.Offset)
        {
            throw new ArgumentException("chunk範囲が不正です。", nameof(request));
        }
        string path = AccessPolicy.ResolvePath(request.Path, true);
        using IDisposable lease = await Operations.AcquireAsync([new(path, false)], request.Operational?.Settings.FileConflictPolicy, cancellationToken).ConfigureAwait(false);
        if (!versions.TryGetValue(path, out (long Length, long WriteTicks, string Hash) version)
            || version.Length != request.ExpectedLength || !string.Equals(version.Hash, request.ExpectedSha256, StringComparison.Ordinal)
            || File.GetLastWriteTimeUtc(path).Ticks != version.WriteTicks)
        {
            throw new IOException("source内容版を再確認してください。");
        }
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        AccessPolicy.ValidatePath(path, true);
        if (input.Length != request.ExpectedLength)
        {
            throw new IOException("sourceのsizeが変更されました。");
        }
        input.Position = request.Offset;
        byte[] bytes = new byte[(int)Math.Min(request.Count, request.ExpectedLength - request.Offset)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (File.GetLastWriteTimeUtc(path).Ticks != version.WriteTicks)
        {
            throw new IOException("sourceが読取り中に変更されました。");
        }
        return new(request.Offset, bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>明示再要求を照合して転送段階を開きます。</summary>
    /// <returns>段階識別子です。</returns>
    public Task<FileStageToken> OpenStageAsync(FileStageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return stages.OpenAsync(request, cancellationToken);
    }

    /// <summary>段階内の相対ディレクトリを作成します。</summary>
    /// <returns>作成完了です。</returns>
    public Task StageDirectoryAsync(FileStageDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return stages.DirectoryAsync(request, cancellationToken);
    }

    /// <summary>内容を照合した分割データを書き込みます。</summary>
    /// <returns>書込み完了です。</returns>
    public Task WriteChunkAsync(FileChunkWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return stages.WriteAsync(request, cancellationToken);
    }

    /// <summary>全マニフェストと内容を検証して宛先へ確定します。</summary>
    /// <returns>確定結果です。</returns>
    public Task<FileStageCommitResult> CommitStageAsync(FileStageToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        return stages.CommitAsync(token, cancellationToken);
    }

    /// <summary>明示された不要段階を破棄します。</summary>
    /// <returns>破棄完了です。</returns>
    public Task DiscardStageAsync(FileStageToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        return stages.DiscardAsync(token, cancellationToken);
    }
}
