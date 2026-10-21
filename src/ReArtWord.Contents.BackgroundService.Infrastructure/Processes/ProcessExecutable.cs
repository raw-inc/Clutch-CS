using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>プロセスの実行パスを、変更権限を要求せず読み取ります。</summary>
public static partial class ProcessExecutable
{
    private static uint QueryLimitedInformation => 0x1000;
    private static int MaximumPathCharacters => 32768;

    /// <summary>読み取れない実体は所有するプロセスと判断しません。</summary>
    /// <returns>取得できた絶対パス、またはnullです。</returns>
    public static string? GetPath(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(process.Id);
            }
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Windowsの照会専用ハンドルを使用します。</summary>
    /// <returns>実行パス、または取得できない場合のnullです。</returns>
    [SupportedOSPlatform("windows")]
    private static string? ReadWindows(int processId)
    {
        using SafeProcessHandle handle = OpenProcess(QueryLimitedInformation, false, processId);
        ushort[] buffer = new ushort[MaximumPathCharacters];
        uint length = (uint)buffer.Length;
        if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, ref buffer[0], ref length))
        {
            return null;
        }
        return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, checked((int)length))));
    }

    /// <summary>照会対象のプロセスを開きます。</summary>
    /// <returns>所有するハンドルです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    /// <summary>Windowsが保持する実行パスを取得します。</summary>
    /// <returns>取得できた場合はtrueです。</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, ref ushort buffer, ref uint length);
}
