using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>ノード試験で原子的比較更新を提供します。</summary>
internal sealed class TestStateStore : INodeStateStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Scope, string Key), VersionedState> values = [];
    private int version;

    /// <summary>指定範囲の現在値を読み取ります。</summary>
    /// <returns>保存済みの値、未保存ならnullです。</returns>
    public ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            values.TryGetValue((scope, key), out VersionedState? result);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>期待版が一致した場合だけ更新します。</summary>
    /// <returns>実際の比較更新結果です。</returns>
    public ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            values.TryGetValue((scope, key), out VersionedState? current);
            if (!string.Equals(current?.Version, expectedVersion, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new StateWriteResult(false, current, EOutcome.Succeeded));
            }
            if (value is null)
            {
                values.Remove((scope, key));
                return ValueTask.FromResult(new StateWriteResult(true, null, EOutcome.Succeeded));
            }
            current = new VersionedState((++version).ToString(CultureInfo.InvariantCulture), value.Copy());
            values[(scope, key)] = current;
            return ValueTask.FromResult(new StateWriteResult(true, current, EOutcome.Succeeded));
        }
    }
}
