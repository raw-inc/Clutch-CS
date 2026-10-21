using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>ローカル複製の遅延と、Coordinatorの確認済み版競合を再現します。</summary>
internal sealed class ConflictingStateStore : INodeStateStore
{
    private readonly TestStateStore inner = new();
    private bool conflict = true;

    /// <summary>確認不能な保存結果を返す障害条件です。</summary>
    internal bool UnknownWrites
    {
        get;
        init;
    }

    /// <summary>結果不明から暗黙再送していないことを確認します。</summary>
    internal int WriteAttempts
    {
        get;
        private set;
    }

    /// <summary>処理中世代がまだローカルに複製されていない場合も再現します。</summary>
    /// <returns>確定結果だけが見える遅延読み取りです。</returns>
    public async ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        VersionedState? current = await inner.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        bool pending = current?.Value.Json.EnumerateArray().Any(entry => entry.TryGetProperty("result", out System.Text.Json.JsonElement result)
            && result.ValueKind == System.Text.Json.JsonValueKind.Null) == true;
        return pending ? null
                       : current;
    }

    /// <summary>一度目に別の更新を挟み、再試行には返却した最新状態を要求します。</summary>
    /// <returns>実環境と同じFailedの版競合、または通常の比較更新です。</returns>
    public async ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
    {
        WriteAttempts++;
        if (UnknownWrites)
        {
            return new(false, null, EOutcome.Unknown, "Persistence.VersionConflict");
        }
        if (conflict)
        {
            conflict = false;
            VersionedState? current = await inner.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            StateWriteResult updated = await inner.CompareExchangeAsync(scope, key, current?.Version, current?.Value ?? NodeValue.FromObject(Array.Empty<object>()), cancellationToken).ConfigureAwait(false);
            return new(false, updated.Current, EOutcome.Failed, "Persistence.VersionConflict");
        }
        conflict = true;
        StateWriteResult saved = await inner.CompareExchangeAsync(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        return saved.Applied ? saved
                             : saved with { Outcome = EOutcome.Failed, Reason = "Persistence.VersionConflict" };
    }
}
