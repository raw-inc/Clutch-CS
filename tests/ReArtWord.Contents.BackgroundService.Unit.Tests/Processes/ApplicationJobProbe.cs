using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>サービス相当の実機試験が親ジョブへ束縛されているかを観測します。</summary>
[SupportedOSPlatform("windows")]
internal static partial class ApplicationJobProbe
{
    /// <summary>テストランナーのジョブ制限を変更せず照会します。</summary>
    /// <returns>所属とWindowsの制限ビットです。</returns>
    internal static (bool InJob, int Limits) Read()
    {
        if (!IsProcessInJob(new(-1), IntPtr.Zero, out bool assigned))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!assigned)
        {
            return (false, 0);
        }
        int length = 48;
        if (Environment.Is64BitProcess)
        {
            length = 64;
        }
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 2, buffer, length, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return (true, Marshal.ReadInt32(buffer, 16));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>呼出元自身のジョブ所属を取得します。</summary>
    /// <returns>照会成功です。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    /// <summary>所属ジョブの既存制限を読み取ります。</summary>
    /// <returns>照会成功です。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(IntPtr job, int informationClass, IntPtr information, int length, IntPtr returned);
}
