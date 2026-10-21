using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>ACL の変更中に対象を入れ替えられないよう、リンクと共有権限を確認します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsInstallationFile
{
    private static uint OpenExisting => 3;
    private static uint BackupSemantics => 0x02000000;
    private static uint OpenReparsePoint => 0x00200000;
    private static int FileStandardInformation => 1;

    /// <summary>削除・書込みとの共有を認めず、同じファイルのハンドルを保持します。</summary>
    /// <returns>呼出元が破棄するハンドルです。</returns>
    internal static SafeFileHandle Open(string path, bool directory)
    {
        FileSystemRights rights = FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        SafeFileHandle handle = CreateFile(path, (uint)rights, FileShare.Read, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if (!GetInformation(handle, FileStandardInformation, out StandardInformation information, (uint)Marshal.SizeOf<StandardInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if (!directory && information.LinkCount != 1)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("Installation files must not have hard links.");
        }
        return handle;
    }

    /// <summary>ハンドルに対して ACL と所有者を設定します。</summary>
    internal static void SetSecurity(SafeFileHandle handle, FileSystemSecurity security)
    {
        uint ownerAndProtectedDacl = 0x80000005;
        if (!SetKernelObjectSecurity(handle, ownerAndProtectedDacl, security.GetSecurityDescriptorBinaryForm()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>ファイルのリンク数と種別を取得する Windows の構造体です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInformation
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint LinkCount;
        internal byte DeletePending;
        internal byte Directory;

        /// <summary>リンク数を表示します。</summary>
        /// <returns>リンク数の説明です。</returns>
        public override readonly string ToString() => FormattableString.Invariant($"Links: {LinkCount}");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetInformation(SafeFileHandle handle, int kind, out StandardInformation information, uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
}
