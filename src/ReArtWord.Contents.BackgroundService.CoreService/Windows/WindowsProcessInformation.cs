using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>Win32 PROCESS_INFORMATIONの固定配置です。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsProcessInformation
{
    /// <summary>呼出元が閉じるプロセスハンドルです。</summary>
    internal IntPtr Process;

    /// <summary>呼出元が閉じるスレッドハンドルです。</summary>
    internal IntPtr Thread;

    /// <summary>生成したプロセスのIDです。</summary>
    internal uint ProcessId;

    /// <summary>生成したメインスレッドのIDです。</summary>
    internal uint ThreadId;

    /// <summary>生成した実体を診断用に表します。</summary>
    /// <returns>プロセスIDとスレッドIDです。</returns>
    public override readonly string ToString() => System.FormattableString.Invariant($"{ProcessId}/{ThreadId}");
}
