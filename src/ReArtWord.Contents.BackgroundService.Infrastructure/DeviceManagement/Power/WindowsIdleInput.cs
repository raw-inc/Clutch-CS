using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>Session 0ではなく全ログオンセッションの最後の入力を確認します。</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsIdleInput
{
    /// <summary>一つでも未確認のセッションがあれば、無操作であると推測しません。</summary>
    /// <returns>最も新しいログオン・接続・入力時刻。確認できない場合はnullです。</returns>
    public static DeviceUserActivity? LastActivity(DeviceUserActivity? console = null)
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr sessions, out int count))
        {
            return null;
        }
        try
        {
            if (count is < 0 or > 1024)
            {
                return null;
            }
            int size = IntPtr.Size == 8 ? 24
                                        : 12;
            int stateOffset = IntPtr.Size == 8 ? 16
                                               : 8;
            DateTimeOffset? latest = null;
            List<string> identities = [];
            for (int index = 0; index < count; index++)
            {
                IntPtr item = IntPtr.Add(sessions, index * size);
                int id = Marshal.ReadInt32(item);
                int state = Marshal.ReadInt32(item, stateOffset);
                if (id == 0 || state is 6 or 8 or 9)
                {
                    continue;
                }
                (bool valid, DeviceUserActivity? activity) = SessionActivity(id, console);
                if (!valid)
                {
                    return null;
                }
                if (activity is not null)
                {
                    identities.Add(activity.Identity);
                    if (latest is null || latest < activity.LastActivity)
                    {
                        latest = activity.LastActivity;
                    }
                }
            }
            return latest is DateTimeOffset time ? new(0, time, ContractJson.Hash(identities.Order(StringComparer.Ordinal).ToArray()))
                                                 : null;
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
    }

    /// <summary>AC状態が未確定なら、どちらのタイマーも使いません。</summary>
    /// <returns>商用電源ならtrue、電池ならfalse、未確認ならnullです。</returns>
    internal static bool? OnAcPower()
    {
        byte[] status = new byte[12];
        if (!GetSystemPowerStatus(status))
        {
            return null;
        }
        return status[0] switch { 0 => false, 1 => true, _ => null };
    }

    /// <summary>WTSINFOEX_LEVEL1のFILETIMEを読みます。</summary>
    /// <returns>読取り成否と利用者が存在する場合の最終入力等です。</returns>
    private static (bool Valid, DeviceUserActivity? Activity) SessionActivity(int id, DeviceUserActivity? console)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, id, 25, out IntPtr data, out int bytes))
        {
            return (false, null);
        }
        try
        {
            // WTSINFOEX: DWORD Level + 8-byte aligned union. Level1's strings precede five LARGE_INTEGER times.
            if (bytes < 232 || Marshal.ReadInt32(data) != 1)
            {
                return (false, null);
            }
            string? user = Marshal.PtrToStringUni(IntPtr.Add(data, 86), 21)?.TrimEnd('\0');
            if (string.IsNullOrEmpty(user))
            {
                return (true, null);
            }
            long input = Marshal.ReadInt64(data, 192), current = Marshal.ReadInt64(data, 200);
            if (input == 0)
            {
                using Process process = Process.GetCurrentProcess();
                DeviceUserActivity? local = id == process.SessionId ? CurrentSession()
                                                                    : console;
                if (local?.SessionId != id || id != WTSGetActiveConsoleSessionId())
                {
                    return (false, null);
                }
                return (true, local with { Identity = $"{local.Identity}:{Marshal.ReadInt64(data, 168)}" });
            }
            long activity = Math.Max(input, Math.Max(Marshal.ReadInt64(data, 168), Marshal.ReadInt64(data, 176)));
            if (input <= 0 || current < activity)
            {
                return (false, null);
            }
            DateTimeOffset sampled = DateTimeOffset.FromFileTime(current);
            if (TimeSpan.FromMinutes(1) < (sampled - DateTimeOffset.UtcNow).Duration())
            {
                return (false, null);
            }
            return (true, new(id, DateTimeOffset.FromFileTime(activity), $"wts:{id}:{activity}"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (false, null);
        }
        finally
        {
            WTSFreeMemory(data);
        }
    }

    /// <summary>コンソールでGetLastInputInfoを呼び、tick値を時刻と別の識別子に保持します。</summary>
    /// <returns>同じ入力を別プロセスでも同じ識別子で表す確認結果です。</returns>
    public static DeviceUserActivity? CurrentSession()
    {
        using Process process = Process.GetCurrentProcess();
        if (process.SessionId == 0 || process.SessionId != WTSGetActiveConsoleSessionId())
        {
            return null;
        }
        byte[] data = new byte[8];
        BitConverter.TryWriteBytes(data, 8);
        if (!GetLastInputInfo(data))
        {
            return null;
        }
        uint input = BitConverter.ToUInt32(data, 4);
        long uptime = Environment.TickCount64;
        uint elapsed = unchecked((uint)uptime - input);
        long inputTick = uptime - elapsed;
        if (inputTick < 0)
        {
            return null;
        }
        return new(process.SessionId, DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(elapsed), $"console:{process.SessionId}:{inputTick}");
    }

    /// <summary>このプロセスの対話セッションの最終入力tickを取得します。</summary>
    /// <returns>確認成否です。</returns>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo([In, Out] byte[] data);

    /// <summary>接続中のコンソールを取得します。</summary>
    /// <returns>セッション識別子です。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WTSGetActiveConsoleSessionId();

    /// <summary>ログオンセッションを列挙します。</summary>
    /// <returns>列挙成否です。</returns>
    [LibraryImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    /// <summary>指定セッションの拡張情報を取得します。</summary>
    /// <returns>照会成否です。</returns>
    [LibraryImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(IntPtr server, int session, int information, out IntPtr buffer, out int bytes);

    /// <summary>WTS所有の領域を解放します。</summary>
    [LibraryImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WTSFreeMemory(IntPtr buffer);

    /// <summary>現在の電源供給を読みます。</summary>
    /// <returns>取得成否です。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus([Out] byte[] status);
}
