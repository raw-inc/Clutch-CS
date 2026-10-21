using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>有効なローカルユーザーの名前・SID・制限をWindowsから取得します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsLoginUsers
{
    /// <summary>機械・信頼アカウントを含めず、列挙を有限件数に制限します。</summary>
    /// <returns>SID順に固定した候補です。</returns>
    internal static IReadOnlyList<DeviceLoginUser> Read()
    {
        List<DeviceLoginUser> users = [];
        nuint resume = 0;
        for (int page = 0; page < 32; page++)
        {
            uint code = NetUserEnum(null, 0, 2, out IntPtr buffer, 65536, out uint count, out _, ref resume);
            try
            {
                if (code is not (0 or 234))
                {
                    throw new Win32Exception(unchecked((int)code));
                }
                if (1024 < count || 1024 < users.Count + count)
                {
                    throw new InvalidOperationException("Device.LoginUserLimit");
                }
                for (int index = 0; index < count; index++)
                {
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, index * IntPtr.Size)) ?? throw new InvalidOperationException("Device.LoginUnavailable");
                    users.Add(ReadUser(name));
                }
                if (code == 0)
                {
                    return users.OrderBy(user => user.Sid, StringComparer.Ordinal).ToArray();
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }
        }
        throw new InvalidOperationException("Device.LoginUserLimit");
    }

    /// <summary>名前を列挙した後、SIDを含むUSER_INFO_23を個別照会します。</summary>
    /// <returns>同じ名前のアカウントが存在している場合の情報です。</returns>
    private static DeviceLoginUser ReadUser(string name)
    {
        uint code = NetUserGetInfo(null, name, 23, out IntPtr data);
        try
        {
            if (code != 0)
            {
                throw new Win32Exception(unchecked((int)code));
            }
            int sidOffset = IntPtr.Size == 8 ? 32
                                             : 16;
            string fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, IntPtr.Size)) ?? string.Empty;
            uint flags = unchecked((uint)Marshal.ReadInt32(data, IntPtr.Size * 3));
            string sid = new SecurityIdentifier(Marshal.ReadIntPtr(data, sidOffset)).Value;
            bool selectable = (flags & (2 | 16 | 0x40000 | 0x800000)) == 0;
            return new(sid, name, fullName, flags, selectable);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                _ = NetApiBufferFree(data);
            }
        }
    }

    /// <summary>USER_INFO_0でローカルアカウント名を列挙します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint NetUserEnum(string? server, uint level, uint filter, out IntPtr buffer, uint maximum, out uint read, out uint total, ref nuint resume);

    /// <summary>SIDを含むアカウント情報を取得します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint NetUserGetInfo(string? server, string user, uint level, out IntPtr buffer);

    /// <summary>列挙されたネイティブ領域を解放します。</summary>
    /// <returns>Win32結果です。</returns>
    [LibraryImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint NetApiBufferFree(IntPtr buffer);
}
