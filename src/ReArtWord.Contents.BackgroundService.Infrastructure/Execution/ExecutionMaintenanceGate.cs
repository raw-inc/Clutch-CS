using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution;

/// <summary>Peer参加と管理APIを維持しながら、更新中の新規通常実行を永続保留します。</summary>
public sealed class ExecutionMaintenanceGate : IDisposable
{
    private readonly IDurableRepository repository;
    private readonly string scope;
    private readonly SemaphoreSlim changes = new(1, 1);
    private MaintenanceState? current;
    private bool loaded;

    /// <summary>ローカルPeerの永続状態を使用します。</summary>
    public ExecutionMaintenanceGate(IDurableRepository repository, string peerId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        this.repository = repository;
        scope = "peer:" + peerId + ":execution-admission";
    }

    /// <summary>所有する操作と理由です。</summary>
    public MaintenanceState? State => Volatile.Read(ref current);

    /// <summary>未読の再起動状態も通常実行を許しません。</summary>
    public bool IsClosed => !Volatile.Read(ref loaded) || State is not null;

    /// <summary>Readyに進む前に再起動を跨ぐ保留状態を復元します。</summary>
    /// <returns>実保存内容の復元です。</returns>
    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableEntry? saved = await repository.ReadAsync(scope, "maintenance", cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref current, saved?.Value?.Json.Deserialize<MaintenanceState>());
            Volatile.Write(ref loaded, true);
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>同じ所有者の再照会は許し、別操作の保留を上書きしません。</summary>
    /// <returns>この操作が新規実行を保留できたかを示します。</returns>
    public async ValueTask<bool> EnterAsync(Guid operationId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!loaded || (State is not null && State.OperationId != operationId))
            {
                return false;
            }
            MaintenanceState state = State ?? new MaintenanceState(operationId, reason, DateTimeOffset.UtcNow);
            await repository.WriteLocalSnapshotAsync(scope, "maintenance", NodeValue.FromObject(state), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref current, state);
            return true;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>保留した同じ操作だけが新規実行を再開できます。</summary>
    /// <returns>現在の所有者に対する解除確認です。</returns>
    public async ValueTask<bool> ExitAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!loaded || (State is not null && State.OperationId != operationId))
            {
                return false;
            }
            await repository.WriteLocalSnapshotAsync(scope, "maintenance", NodeValue.Null, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref current, null);
            return true;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>所有する変更排他を解放します。</summary>
    public void Dispose() => changes.Dispose();
}
