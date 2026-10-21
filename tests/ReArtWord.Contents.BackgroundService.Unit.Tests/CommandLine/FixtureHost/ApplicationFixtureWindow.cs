using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.CommandLineFixture;

/// <summary>本番アプリに触れず、初期表示と通常終了を観測する小さい実ウィンドウです。</summary>
[SupportedOSPlatform("windows")]
internal static partial class ApplicationFixtureWindow
{
    /// <summary>ウィンドウを所有する同じスレッドでメッセージを処理します。</summary>
    internal static void Run(int delay)
    {
        IntPtr window = CreateWindowExW(0, "STATIC", "Clutch-CS isolated application test", 0x00cf0000, 30, 30, 360, 90, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            ShowWindow(window, 10);
            using Process self = Process.GetCurrentProcess();
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            string path = Path.Combine(AppContext.BaseDirectory, self.Id + ".window.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                self.SessionId, UserId = user.User?.Value, Visible = IsWindowVisible(window), HasConsole = GetConsoleWindow() != IntPtr.Zero,
                Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory = Environment.CurrentDirectory,
                Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray(),
            }));
            File.Move(path + ".tmp", path);
            Stopwatch elapsed = Stopwatch.StartNew();
            string release = Path.Combine(AppContext.BaseDirectory, "release");
            while (IsWindow(window) && !File.Exists(release) && (delay < 0 || elapsed.ElapsedMilliseconds < delay))
            {
                while (PeekMessageW(out WindowMessage message, IntPtr.Zero, 0, 0, 1))
                {
                    _ = TranslateMessage(in message);
                    _ = DispatchMessageW(in message);
                }
                Thread.Sleep(10);
            }
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    /// <summary>OS組込みクラスで試験用のトップレベルウィンドウを作成します。</summary>
    /// <returns>このプロセスだけが所有するハンドルです。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateWindowExW(uint extended, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int mode);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    /// <summary>コンソールの有無を試験対象自身のセッションで取得します。</summary>
    /// <returns>コンソールウィンドウ、または未作成のゼロです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out WindowMessage message, IntPtr window, uint minimum, uint maximum, uint remove);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in WindowMessage message);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr DispatchMessageW(in WindowMessage message);

    /// <summary>Win32のMSG配置です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        internal IntPtr Window;
        internal uint Message;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;

        /// <summary>診断ではイベント種別だけを返します。</summary>
        /// <returns>Windowsメッセージ番号です。</returns>
        public override readonly string ToString() => Message.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
