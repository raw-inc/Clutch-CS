using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
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
    private static string ClockKey => "IdleShutdownClock";
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
        if (SystemBootObservation.Read() is { } boot)
        {
            SaveEnableMark(key, policy.Version, boot);
        }
        key.SetValue("IdleShutdown", JsonSerializer.Serialize(policy, ContractJson.Options), RegistryValueKind.String);
        key.Flush();
    }

    /// <summary>同じOS起動回では保存済みの経過時間を使い、再起動後は今回の起動からの時間を下限にします。</summary>
    /// <returns>有効化後に確実に経過した時間です。OS起動回が不明なら監視開始から計測します。</returns>
    internal static TimeSpan? EnableAge(DeviceIdleShutdownPolicy policy)
    {
        if (SystemBootObservation.Read() is not { } boot)
        {
            return null;
        }
        using RegistryKey? saved = Registry.LocalMachine.OpenSubKey(StorePath);
        if (saved?.GetValue(ClockKey) is string json && JsonSerializer.Deserialize<EnableMark>(json, ContractJson.Options) is { } mark && mark.Version == policy.Version)
        {
            if (string.Equals(mark.Boot.Id, boot.Id, StringComparison.Ordinal))
            {
                return boot.Uptime - mark.Boot.Uptime;
            }
            return boot.Uptime;
        }

        // 旧設定には起動回がありません。最初に確認した時点を保存し、Coreが再起動してもそこから待てるようにします。
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(StorePath);
            SaveEnableMark(key, policy.Version, boot);
            key.Flush();
            return TimeSpan.Zero;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>旧Coreが読む設定JSONとは別の値に、同じ設定版の有効化位置を保存します。</summary>
    private static void SaveEnableMark(RegistryKey key, Guid version, SystemBootSession boot)
        => key.SetValue(ClockKey, JsonSerializer.Serialize(new EnableMark(version, boot), ContractJson.Options), RegistryValueKind.String);

    /// <summary>設定が有効になった時点のOS起動回と稼働時間です。</summary>
    private sealed record EnableMark(Guid Version, SystemBootSession Boot);

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
