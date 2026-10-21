using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>生成したネイティブ境界でUnicode、出力領域の上限、Win32エラーを維持することを確認します。</summary>
public sealed partial class NativeInteropTests
{
    /// <summary>本番で使用するWindowsの宣言を実DLLのexportと照合し、暗黙のW接尾辞への依存を残しません。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void WindowsLibraryImportsResolveTheirDeclaredEntryPoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assembly[] assemblies = [typeof(WindowsTrayNative).Assembly, typeof(WindowsResourceProbe).Assembly, typeof(BoundedProcessRunner).Assembly];
        string unixLibrary = "libc";
        Dictionary<string, IntPtr> libraries = new(StringComparer.OrdinalIgnoreCase);
        int checkedImports = 0;
        try
        {
            foreach (MethodInfo method in assemblies.SelectMany(assembly => assembly.GetTypes()).SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
            {
                LibraryImportAttribute? import = method.GetCustomAttribute<LibraryImportAttribute>();
                if (import is null || string.Equals(import.LibraryName, unixLibrary, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!libraries.TryGetValue(import.LibraryName, out IntPtr library))
                {
                    library = NativeLibrary.Load(import.LibraryName, method.DeclaringType!.Assembly, DllImportSearchPath.System32);
                    libraries.Add(import.LibraryName, library);
                }
                string entry = import.EntryPoint ?? method.Name;
                Assert.True(NativeLibrary.TryGetExport(library, entry, out _), import.LibraryName + "!" + entry);
                checkedImports++;
            }
            Assert.True(0 < checkedImports);
        }
        finally
        {
            foreach (IntPtr library in libraries.Values)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    /// <summary>表示しないwindowの日本語名を読み、指定文字数より後の領域を変更しません。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void WindowTextPreservesUnicodeAndCallerBufferBounds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string title = "ClutchCS 日本語 🖥 " + Guid.NewGuid().ToString("N");
        IntPtr window = WindowsTrayNative.CreateWindowExW(0, "STATIC", title, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            char[] buffer = Enumerable.Repeat('\u2603', title.Length + 9).ToArray();
            int copied = WindowsTrayNative.GetWindowTextUnsafe(window, buffer, title.Length + 1);
            Assert.Equal(title, new string(buffer, 0, copied));
            Assert.Equal('\0', buffer[copied]);
            Assert.All(buffer.Skip(title.Length + 1), value => Assert.Equal('\u2603', value));
        }
        finally
        {
            Assert.True(WindowsTrayNative.DestroyWindow(window));
        }
    }

    /// <summary>Unicode名の構造体登録を確認し、後片付けが元の登録失敗コードを消しません。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void WindowClassPreservesUnicodeAndDuplicateRegistrationError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        WindowsTrayNative.WindowProcedure procedure = WindowsTrayNative.DefWindowProcW;
        WindowsTrayNative.WindowClass definition = new()
        {
            Size = (uint)Marshal.SizeOf<WindowsTrayNative.WindowClass>(), Name = "ClutchCS.日本語." + Guid.NewGuid().ToString("N"),
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = WindowsTrayNative.GetModuleHandleW(null),
        };
        Assert.NotEqual((ushort)0, WindowsTrayNative.RegisterClassUnsafe(ref definition));
        try
        {
            Assert.Equal((ushort)0, WindowsTrayNative.RegisterClassUnsafe(ref definition));
            Assert.Equal(1410, Marshal.GetLastPInvokeError());
            IntPtr window = WindowsTrayNative.CreateWindowExW(0, definition.Name, string.Empty, 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, definition.Instance, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, window);
            Assert.True(WindowsTrayNative.DestroyWindow(window));
        }
        finally
        {
            Assert.True(UnregisterClassW(definition.Name, definition.Instance));
            GC.KeepAlive(procedure);
        }
    }

    /// <summary>現在利用者のprofile名を二段階で取得し、必要文字数と終端、出力上限を確認します。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void ProfileDirectoryPreservesRequiredCharacterCountAndBufferBounds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        int length = 0;
        Assert.False(WindowsProcessNative.GetUserProfileDirectoryUnsafe(identity.AccessToken, [], ref length));
        Assert.Equal(122, Marshal.GetLastPInvokeError());
        Assert.True(1 < length);
        int capacity = length;
        char[] buffer = Enumerable.Repeat('\u2603', capacity + 8).ToArray();
        Assert.True(WindowsProcessNative.GetUserProfileDirectoryUnsafe(identity.AccessToken, buffer, ref length));
        int end = Array.IndexOf(buffer, '\0', 0, capacity);
        Assert.True(0 < end);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), new string(buffer, 0, end), ignoreCase: true);
        Assert.All(buffer.Skip(capacity), value => Assert.Equal('\u2603', value));
    }

    /// <summary>試験が登録したwindow classだけを解放します。</summary>
    /// <returns>登録を削除できたかです。</returns>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(string name, IntPtr instance);
}
