using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Windows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.WakeOnLan;

/// <summary>WindowsのNIC列挙と、既存のPC共通レジストリ形式を保持します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsWakeSettings
{
    private static string StorePath => @"SOFTWARE\ReArtWord\Contents\WakeOnLan";

    /// <summary>専用の読み取りスクリプトから有限のNIC一覧を取得します。</summary>
    /// <returns>MAC・物理NIC判定・IPv4ネットワークです。</returns>
    internal static async Task<DeviceWakeInventory> ReadInventoryAsync(CancellationToken cancellationToken = default)
    {
        DevicePowerShellReply reply = await DevicePowerShell.RunAsync("WakeOnLan", new { mode = "read", query = new { } }, cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
        {
            throw new InvalidOperationException(reply.Reason ?? "Device.ReadFailed");
        }
        return DeviceSnapshots.Read<DeviceWakeInventory>(reply.Data);
    }

    /// <summary>未設定だけを物理NICすべての既定値とします。</summary>
    /// <returns>明示保存された選択、または既定です。</returns>
    internal static DeviceWakeSelection ReadSelection()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(StorePath);
        object? value = key?.GetValue("Selection");
        if (value is null)
        {
            return new(true, []);
        }
        if (value is not string json || 16384 < json.Length)
        {
            throw new InvalidOperationException("Device.InvalidWakeSelection");
        }
        return JsonSerializer.Deserialize<DeviceWakeSelection>(json, ContractJson.Options) ?? throw new InvalidOperationException("Device.InvalidWakeSelection");
    }

    /// <summary>PC共通の一つのレジストリ値として選択を確定します。</summary>
    internal static void WriteSelection(DeviceWakeSelection selection)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(StorePath);
        key.SetValue("Selection", JsonSerializer.Serialize(selection, ContractJson.Options), RegistryValueKind.String);
        key.Flush();
    }
}
