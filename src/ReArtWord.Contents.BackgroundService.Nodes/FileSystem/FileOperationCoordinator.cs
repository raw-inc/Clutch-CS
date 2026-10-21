using ReArtWord.Contents.BackgroundService.Contracts.Files;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>重なる変更と読取りを排他化し、安全な読取り同士を並行実行します。</summary>
public sealed class FileOperationCoordinator
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, IReadOnlyList<FileAccessScope>> active = [];
    private readonly EFileConflictPolicy policy;
    private readonly StringComparison comparison;
    private TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>クラスタ共通の競合方針を固定します。</summary>
    public FileOperationCoordinator(EFileConflictPolicy policy)
    {
        this.policy = policy;
        comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
    }

    /// <summary>全範囲を同時に取得して、複数パスの順序待ちdeadlockを防ぎます。</summary>
    /// <returns>呼出側が解放するリースです。</returns>
    public Task<IDisposable> AcquireAsync(IReadOnlyList<FileAccessScope> scopes, CancellationToken cancellationToken = default) => AcquireAsync(scopes, null, cancellationToken);

    /// <summary>取得開始時に固定した共通方針で待機または即時拒否します。</summary>
    /// <returns>同じ一件が解放するリースです。</returns>
    public async Task<IDisposable> AcquireAsync(IReadOnlyList<FileAccessScope> scopes, EFileConflictPolicy? conflictPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        EFileConflictPolicy fixedPolicy = conflictPolicy ?? policy;
        if (!Enum.IsDefined(fixedPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }
        FileAccessScope[] fixedScopes = scopes.ToArray();
        while (!cancellationToken.IsCancellationRequested)
        {
            TaskCompletionSource signal;
            using (gate.EnterScope())
            {
                bool conflict = active.Values.Any(held => held.Any(first => fixedScopes.Any(second => Overlaps(first, second))));
                if (!conflict)
                {
                    Guid id = Guid.NewGuid();
                    active.Add(id, fixedScopes);
                    return new FileAccessLease(() => Release(id));
                }
                if (fixedPolicy == EFileConflictPolicy.Fail)
                {
                    throw new IOException("file.conflict");
                }
                signal = released;
            }
            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>片方が変更しパスが包含関係にある場合だけ競合します。</summary>
    /// <returns>同時実行できないかです。</returns>
    private bool Overlaps(FileAccessScope first, FileAccessScope second)
    {
        if (!first.Write && !second.Write)
        {
            return false;
        }
        return string.Equals(first.Path, second.Path, comparison)
            || first.Path.StartsWith(second.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
            || second.Path.StartsWith(first.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>待機中の操作へ範囲解放を通知します。</summary>
    private void Release(Guid id)
    {
        using (gate.EnterScope())
        {
            active.Remove(id);
            TaskCompletionSource previous = released;
            released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }
}
