using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>Console の切断エラー抑止を避け、pipe 切断を IOException として受け取ります。</summary>
internal static partial class StandardOutputDangerous
{
    /// <summary>プロセスが所有する標準出力を、所有権を移さずバイナリで開きます。</summary>
    /// <returns>切断を報告する非バッファ出力。</returns>
    internal static Stream Open()
    {
        nint handle = OperatingSystem.IsWindows() ? GetStandardHandle(-11)
                                                  : 1;
        if (handle == 0 || handle == -1)
        {
            throw new IOException("Standard output is unavailable.");
        }

        // 終了時に同期 pipe 書き込みが残っても、Dispose の flush 待ちでプロセス終了を止めません。
        SafeFileHandle? borrowed = new(handle, false);
        try
        {
            FileStream stream = new(borrowed, FileAccess.Write, 1, false);
            borrowed = null;
            return stream;
        }
        finally
        {
            borrowed?.Dispose();
        }
    }

    /// <summary>Windows の標準ハンドルを借用します。</summary>
    /// <returns>プロセス所有のハンドル。</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStandardHandle(int kind);
}
