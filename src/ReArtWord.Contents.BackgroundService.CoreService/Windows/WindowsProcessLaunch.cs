using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>対話デスクトップへの起動、引数の引用、ネイティブハンドルの移譲を一つに集約します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessLaunch
{
    /// <summary>一時停止で作成し、監視ハンドルを確保してから実行を開始します。</summary>
    /// <returns>呼出元が終了・破棄を所有するプロセスです。</returns>
    internal static Process Start(SafeAccessTokenHandle? token, string executable, IReadOnlyList<string> arguments, string directory,
        string desktopName = "default", bool noWindow = true, bool hideWindow = false, Action? createdCallback = null)
    {
        string commandLine = string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
        if (32766 < commandLine.Length || commandLine.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The application command line is too long or contains a null character.", nameof(arguments));
        }
        int size = 68, flagsOffset = 44;
        if (Environment.Is64BitProcess)
        {
            size = 104;
            flagsOffset = 60;
        }
        IntPtr startup = Marshal.AllocHGlobal(size);
        IntPtr desktop = Marshal.StringToHGlobalUni("winsta0\\" + desktopName);
        IntPtr command = Marshal.StringToHGlobalUni(commandLine);
        IntPtr environment = IntPtr.Zero;
        try
        {
            Marshal.Copy(new byte[size], 0, startup, size);
            Marshal.WriteInt32(startup, size);
            Marshal.WriteIntPtr(startup, 2 * IntPtr.Size, desktop);
            if (hideWindow)
            {
                Marshal.WriteInt32(startup, flagsOffset, 1);
                Marshal.WriteInt16(startup, flagsOffset + sizeof(int), 0);
            }
            uint flags = 0x00000414;
            if (noWindow)
            {
                flags = 0x08000404;
            }
            WindowsProcessInformation process;
            bool created;
            if (token is null)
            {
                // 実行ファイル名だけの指定は、Win32の拡張子補完とPATH検索を使います。
                string? application = executable;
                if (string.Equals(Path.GetFileName(executable), executable, StringComparison.Ordinal))
                {
                    application = null;
                }
                created = WindowsProcessNative.CreateProcessW(application, command, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, directory, startup, out process);
            }
            else
            {
                if (!WindowsProcessNative.CreateEnvironmentBlock(out environment, token, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                created = WindowsProcessNative.CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false, flags, environment, directory, startup, out process);
            }
            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            Process? owned = null;
            bool resumed = false;
            try
            {
                createdCallback?.Invoke();
                owned = Process.GetProcessById(checked((int)process.ProcessId));
                _ = owned.SafeHandle;
                _ = owned.SessionId;
                if (WindowsProcessNative.ResumeThread(process.Thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                resumed = true;
                return owned;
            }
            finally
            {
                if (!resumed)
                {
                    _ = WindowsProcessNative.TerminateProcess(process.Process, 1);
                    owned?.Dispose();
                }
                _ = WindowsProcessNative.CloseHandle(process.Thread);
                _ = WindowsProcessNative.CloseHandle(process.Process);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                _ = WindowsProcessNative.DestroyEnvironmentBlock(environment);
            }
            Marshal.FreeHGlobal(command);
            Marshal.FreeHGlobal(desktop);
            Marshal.FreeHGlobal(startup);
        }
    }

    /// <summary>Win32 argvの引用符と末尾backslashをescapeします。</summary>
    /// <returns>一つのargv要素です。</returns>
    private static string Quote(string value)
    {
        if (value.Length != 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }
        string escaped = System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"");
        int trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + escaped + new string('\\', trailing) + "\"";
    }
}
