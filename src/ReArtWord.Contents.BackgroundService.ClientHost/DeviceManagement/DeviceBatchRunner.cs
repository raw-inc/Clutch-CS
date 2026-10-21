using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>PCごとに順序を維持し、不明なPCの後続操作だけを止めます。</summary>
internal sealed class DeviceBatchRunner(IDeviceRemote remote, DeviceBatchStore store)
{
    /// <summary>送信前の不明記録を永続化し、未開始・途中終了を区別します。</summary>
    /// <returns>固定計画の最終状態です。</returns>
    internal async Task<DeviceBatchState> RunAsync(DeviceBatchState state, CancellationToken cancellationToken = default)
    {
        DeviceChangeResult?[] results = state.Results.ToArray();
        HashSet<string> blocked = new(StringComparer.Ordinal);
        try
        {
            for (int index = 0; index < state.Request.Changes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DevicePlannedChange change = state.Request.Changes[index];
                Guid id = change.Request.OperationId;
                if (blocked.Contains(change.Target.HostId))
                {
                    results[index] = new(id, EOutcome.NotExecuted, "Device.PreviousChangeUnconfirmed");
                }
                else
                {
                    results[index] = new(id, EOutcome.Unknown, "Device.Dispatching");
                    await store.SaveAsync(state with { Results = results.ToArray() }, cancellationToken).ConfigureAwait(false);
                    DeviceChangeResult result = await remote.ApplyAsync(change, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome == EOutcome.Unknown)
                    {
                        blocked.Add(change.Target.HostId);
                    }
                    results[index] = result;
                }
                await store.SaveAsync(state with { Results = results.ToArray() }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            // 送信後の結果保存に失敗しても、送信前に記録した不明を再実行しません。
        }
        for (int index = 0; index < results.Length; index++)
        {
            results[index] ??= new(state.Request.Changes[index].Request.OperationId, EOutcome.NotExecuted, "Device.BatchInterrupted");
        }
        DeviceBatchState completed = state with { Running = false, Results = results };
        using CancellationTokenSource saving = new(TimeSpan.FromSeconds(10));
        await store.SaveAsync(completed, saving.Token).ConfigureAwait(false);
        return completed;
    }
}
