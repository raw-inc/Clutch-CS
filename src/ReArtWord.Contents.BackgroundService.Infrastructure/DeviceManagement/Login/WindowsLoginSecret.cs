using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>Winlogonが使用するDefaultPasswordだけをLSAで保護します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsLoginSecret
{
    /// <summary>秘密値を公開せず、存在だけを確認して読み出した領域を消去します。</summary>
    /// <returns>読取り権限がない場合はnullです。</returns>
    internal static bool? Exists()
    {
        uint opened = Open(4, out IntPtr policy);
        if (opened == 5)
        {
            return null;
        }
        Check(opened);
        try
        {
            using LoginPasswordBuffer name = new("DefaultPassword".ToCharArray());
            LoginPasswordBuffer.LsaText key = name.Value;
            uint status = LsaNtStatusToWinError(LsaRetrievePrivateData(policy, in key, out IntPtr data));
            try
            {
                if (status is 2 or 3)
                {
                    return false;
                }
                if (status == 5)
                {
                    return null;
                }
                Check(status);
                return true;
            }
            finally
            {
                if (data != IntPtr.Zero)
                {
                    LoginPasswordBuffer.LsaText secret = Marshal.PtrToStructure<LoginPasswordBuffer.LsaText>(data);
                    if (secret.Buffer != IntPtr.Zero)
                    {
                        Marshal.Copy(new byte[secret.Length], 0, secret.Buffer, secret.Length);
                    }
                    _ = LsaFreeMemory(data);
                }
            }
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    /// <summary>平文レジストリ値を作らず、保護された秘密値を保存または削除します。</summary>
    internal static void Store(char[]? password)
    {
        Check(Open(32, out IntPtr policy));
        try
        {
            using LoginPasswordBuffer name = new("DefaultPassword".ToCharArray());
            LoginPasswordBuffer.LsaText key = name.Value;
            if (password is null)
            {
                uint removed = LsaNtStatusToWinError(LsaDeletePrivateData(policy, in key, IntPtr.Zero));
                if (removed is not (2 or 3))
                {
                    Check(removed);
                }
                return;
            }
            using LoginPasswordBuffer buffer = new(password);
            LoginPasswordBuffer.LsaText value = buffer.Value;
            Check(LsaNtStatusToWinError(LsaStorePrivateData(policy, in key, in value)));
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    /// <summary>ローカルLSAの必要な権限だけを要求します。</summary>
    /// <returns>Win32形式の成否です。</returns>
    private static uint Open(uint access, out IntPtr policy)
    {
        int size = IntPtr.Size == 8 ? 48
                                    : 24;
        byte[] attributes = new byte[size];
        BitConverter.TryWriteBytes(attributes, size);
        return LsaNtStatusToWinError(LsaOpenPolicy(IntPtr.Zero, attributes, access, out policy));
    }

    /// <summary>秘密値を例外本文へ含めず、OSエラーだけを伝えます。</summary>
    private static void Check(uint error)
    {
        if (error != 0)
        {
            throw new Win32Exception(unchecked((int)error));
        }
    }

    /// <summary>ローカルPolicyを開きます。</summary>
    /// <returns>NTSTATUSです。</returns>
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaOpenPolicy(IntPtr system, [In] byte[] attributes, uint access, out IntPtr policy);

    /// <summary>Winlogonの秘密値を保存します。</summary>
    /// <returns>NTSTATUSです。</returns>
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaStorePrivateData(IntPtr policy, in LoginPasswordBuffer.LsaText key, in LoginPasswordBuffer.LsaText value);

    /// <summary>nullデータを指定してWinlogonの秘密値を削除します。</summary>
    /// <returns>NTSTATUSです。</returns>
    [LibraryImport("advapi32.dll", EntryPoint = "LsaStorePrivateData")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaDeletePrivateData(IntPtr policy, in LoginPasswordBuffer.LsaText key, IntPtr value);

    /// <summary>秘密値の存在を確認するための一時領域を取得します。</summary>
    /// <returns>NTSTATUSです。</returns>
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaRetrievePrivateData(IntPtr policy, in LoginPasswordBuffer.LsaText key, out IntPtr value);

    /// <summary>取得済み領域を解放します。</summary>
    /// <returns>NTSTATUSです。</returns>
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaFreeMemory(IntPtr memory);

    /// <summary>Policyを閉じます。</summary>
    /// <returns>NTSTATUSです。</returns>
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaClose(IntPtr policy);

    /// <summary>NTSTATUSを公開可能なOSエラーへ変換します。</summary>
    /// <returns>Win32エラーです。</returns>
    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint LsaNtStatusToWinError(uint status);
}
