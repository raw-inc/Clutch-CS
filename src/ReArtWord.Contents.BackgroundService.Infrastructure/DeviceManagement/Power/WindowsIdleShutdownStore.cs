using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>全Coreが共有する管理者用レジストリに設定と実行開始を保存します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsIdleShutdownStore
{
    private static string StorePath => @"SOFTWARE\ReArtWord\Contents\Power";

    /// <summary>未設定だけを既定の無効として扱い、不正な設定からは起動しません。</summary>
    /// <returns>最後に明示保存されたPC全体の設定です。</returns>
    internal static DeviceIdleShutdownPolicy Read()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(StorePath);
        object? value = key?.GetValue("IdleShutdown");
        if (value is null)
        {
            return new();
        }
        if (value is not string json || 4096 < json.Length)
        {
            throw new InvalidOperationException("Device.PowerReadFailed");
        }
        DeviceIdleShutdownPolicy? policy = JsonSerializer.Deserialize<DeviceIdleShutdownPolicy>(json, ContractJson.Options);
        if (policy is null || policy.AcSeconds is > 604800 || policy.DcSeconds is > 604800 || policy.Version == Guid.Empty || policy.EnabledAt == default)
        {
            throw new InvalidOperationException("Device.PowerReadFailed");
        }
        return policy;
    }

    /// <summary>AC/DCと有効化時刻を一つの値として永続化します。</summary>
    internal static void Write(DeviceIdleShutdownPolicy policy)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(StorePath);
        key.SetValue("IdleShutdown", JsonSerializer.Serialize(policy, ContractJson.Options), RegistryValueKind.String);
        key.Flush();
    }

    /// <summary>同じ無操作期間の開始記録を確認し、OS要求前に次の開始を永続化します。</summary>
    /// <returns>初回の要求だけtrueです。呼出側はPC全体の排他を保持します。</returns>
    internal static bool Claim(string episode)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(StorePath);
        string name = "ShutdownAttempt";
        if (string.Equals(key.GetValue(name) as string, episode, StringComparison.Ordinal))
        {
            return false;
        }
        key.SetValue(name, episode, RegistryValueKind.String);
        key.Flush();
        return true;
    }
}
