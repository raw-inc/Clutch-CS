using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>受付内容と開始状態を原子的に保存し、再起動時には送信を再開しません。</summary>
internal sealed class DeviceBatchStore : IDisposable
{
    private readonly string directory;
    private readonly SemaphoreSlim access = new(1, 1);

    /// <summary>ClientHostのローカル永続領域を使用します。</summary>
    internal DeviceBatchStore(string stateDirectory)
    {
        directory = Path.Combine(stateDirectory, "host-manager");
        Directory.CreateDirectory(directory);
    }

    /// <summary>保存された全操作を読み、未確認Hostを履歴の表示件数で落としません。</summary>
    /// <returns>元要求と結果です。</returns>
    internal async Task<IReadOnlyList<DeviceBatchState>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DeviceBatchState> states = [];
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                states.Add(await ReadFileAsync(path, cancellationToken).ConfigureAwait(false));
            }
            return states.OrderByDescending(state => state.CreatedAt).ToArray();
        }
        finally
        {
            access.Release();
        }
    }

    /// <summary>元IDの結果だけを読みます。</summary>
    /// <returns>未受付ならnullです。</returns>
    internal async Task<DeviceBatchState?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(directory, id.ToString("N") + ".json");
            return File.Exists(path) ? await ReadFileAsync(path, cancellationToken).ConfigureAwait(false)
                                     : null;
        }
        finally
        {
            access.Release();
        }
    }

    /// <summary>開始済みは不明、未開始は未実行として確定し、副作用を再送しません。</summary>
    /// <returns>中断記録の保存です。</returns>
    internal async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (DeviceBatchState state in await ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (state.Running)
            {
                DeviceChangeResult?[] results = state.Results.Select((result, index) => result
                    ?? new DeviceChangeResult(state.Request.Changes[index].Request.OperationId, EOutcome.NotExecuted, "Device.BatchInterrupted")).ToArray();
                await SaveAsync(state with { Running = false, Results = results }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>送信前に必ず保存できたことを確認します。</summary>
    /// <returns>原子的置換の完了です。</returns>
    internal async Task SaveAsync(DeviceBatchState state, CancellationToken cancellationToken = default)
    {
        await access.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(directory, state.Request.OperationId.ToString("N") + ".json");
            string temporary = path + ".tmp";
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            access.Release();
        }
    }

    /// <summary>Windowsの置換中に同じ履歴を開かないための排他を解放します。</summary>
    public void Dispose() => access.Dispose();

    /// <summary>不正な保存内容を未受付と誤認しません。</summary>
    /// <returns>型を検証した保存内容です。</returns>
    private static async Task<DeviceBatchState> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<DeviceBatchState>(stream, ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Device.BatchUnreadable");
    }
}
