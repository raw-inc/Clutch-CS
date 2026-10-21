using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;

/// <summary>PC共通のNIC選択を保存し、重いOS一覧照会は短時間再利用します。</summary>
internal sealed class WakeSettings : IDisposable
{
    private readonly Func<CancellationToken, Task<DeviceWakeInventory>> readInventory;
    private readonly Func<DeviceWakeSelection> readSelection;
    private readonly Action<DeviceWakeSelection> writeSelection;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeviceWakeInventory? inventory;
    private long expiresUtcTicks;
    private int inventoryGeneration;
    private int cachedGeneration;

    /// <summary>OSの一覧と、同じPCの全Coreで共有する保存先を使います。</summary>
    internal WakeSettings()
    {
        clock = TimeProvider.System;
        if (OperatingSystem.IsWindows())
        {
            readInventory = WindowsWakeSettings.ReadInventoryAsync;
            readSelection = WindowsWakeSettings.ReadSelection;
            writeSelection = WindowsWakeSettings.WriteSelection;
        }
        else
        {
            WakeSelectionFile file = new(Path.Combine(DeviceHostStorage.DirectoryPath, "wake-selection.json"));
            readInventory = UnixWakeInventory.ReadAsync;
            readSelection = file.Read;
            writeSelection = file.Write;
        }
    }

    /// <summary>OSを変更せずに保存・版・キャッシュ寿命を検証する境界です。</summary>
    internal WakeSettings(Func<CancellationToken, Task<DeviceWakeInventory>> inventoryReader, Func<DeviceWakeSelection> selectionReader,
        Action<DeviceWakeSelection> selectionWriter, TimeProvider timeProvider)
    {
        readInventory = inventoryReader;
        readSelection = selectionReader;
        writeSelection = selectionWriter;
        clock = timeProvider;
    }

    /// <summary>選択は毎回読み、NIC一覧の再照会だけを5分間抑えます。</summary>
    /// <returns>現在の選択と、確認済み一覧の内容版です。</returns>
    internal async Task<DeviceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (inventory is null || cachedGeneration != Volatile.Read(ref inventoryGeneration) || Volatile.Read(ref expiresUtcTicks) <= clock.GetUtcNow().UtcTicks)
            {
                int generation = Volatile.Read(ref inventoryGeneration);
                inventory = await readInventory(cancellationToken).ConfigureAwait(false);
                cachedGeneration = generation;
                if (generation == Volatile.Read(ref inventoryGeneration))
                {
                    Interlocked.Exchange(ref expiresUtcTicks, clock.GetUtcNow().AddMinutes(5).UtcTicks);
                }
            }
            DeviceWakeState state = new(readSelection(), inventory);
            state.Validate();
            return DeviceSnapshots.Create(state);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>OSの電源設定は変えず、表示済みNICの選択だけを保存します。</summary>
    /// <returns>選択の永続化結果です。</returns>
    internal DeviceChangeResult Apply(DeviceChangeRequest request, DeviceSnapshot before)
    {
        DeviceWakeSelection selection = DeviceSnapshots.Read<DeviceWakeSelection>(request.Change);
        selection.Validate();
        DeviceWakeState state = DeviceSnapshots.Read<DeviceWakeState>(before.Data);
        if (selection.InterfaceIds.Any(id => !state.Inventory.Interfaces.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.WakeInterfaceChanged");
        }
        writeSelection(selection);
        return new(request.OperationId, EOutcome.Succeeded, CompletedSteps: ["wake.selection.saved"]);
    }

    /// <summary>ネットワークを本システムから変更した場合は次回の一覧を取り直します。</summary>
    internal void Invalidate()
    {
        Interlocked.Increment(ref inventoryGeneration);
        Interlocked.Exchange(ref expiresUtcTicks, 0);
    }

    /// <summary>所有する読取りの排他を解放します。</summary>
    public void Dispose() => gate.Dispose();
}
