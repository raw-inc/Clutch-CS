using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>Windowsのユーザー権限とプロセス起動の共通APIです。</summary>
internal static partial class WindowsProcessNative
{
    /// <summary>確認済みのプロセスが持つユーザートークンを取得します。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    /// <summary>自身のトークンをOSのLUA規則で通常ユーザーへ制限します。</summary>
    /// <returns>呼出元が解放する制限付きトークンを作成できたかです。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateRestrictedToken(SafeAccessTokenHandle source, uint flags, uint disabledCount, IntPtr disabled,
        uint deletedCount, IntPtr deleted, uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle token);

    /// <summary>接続した補助プロセスのIDをOSから取得します。</summary>
    /// <returns>pipeの接続元を確認できたかです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    /// <summary>利用者の元トークンを変更せず、起動専用のプライマリトークンを複製します。</summary>
    /// <returns>複製成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(SafeAccessTokenHandle source, uint access, IntPtr attributes, int impersonation, int type, out SafeAccessTokenHandle token);

    /// <summary>コンソールへログオンしたセッションを取得します。</summary>
    /// <returns>未ログオンの場合はuint.MaxValueです。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WTSGetActiveConsoleSessionId();

    /// <summary>Systemサービスから利用者のトークンを取得します。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);

    /// <summary>UACで関連付けられたトークンを取得します。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returned);

    /// <summary>複製されたトークンのコンソールセッションを設定します。</summary>
    /// <returns>設定成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref int information, int length);

    /// <summary>起動専用トークンの整合性ラベルを設定します。</summary>
    /// <returns>設定成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length);

    /// <summary>利用者自身の環境変数を作成します。</summary>
    /// <returns>作成成功です。</returns>
    [LibraryImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    /// <summary>作成した環境変数ブロックを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [LibraryImport("userenv.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(IntPtr environment);

    /// <summary>利用者のプロファイルフォルダーを取得します。</summary>
    /// <returns>取得成功です。</returns>
    internal static unsafe bool GetUserProfileDirectoryUnsafe(SafeAccessTokenHandle token, char[] directory, ref int length)
    {
        fixed (char* buffer = directory)
        {
            return ReadUserProfileDirectoryUnsafe(token, buffer, ref length);
        }
    }

    /// <summary>固定したUTF-16領域へ、文字数単位の上限で利用者のプロファイルを読みます。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadUserProfileDirectoryUnsafe(SafeAccessTokenHandle token, char* directory, ref int length);

    /// <summary>指定したトークン・環境・desktopでプロセスを作成します。</summary>
    /// <returns>作成成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, IntPtr command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, IntPtr startup, out WindowsProcessInformation process);

    /// <summary>現在の権限を保ち、指定desktop上で子プロセスを作成します。</summary>
    /// <returns>作成成功です。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessW(string? application, IntPtr command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, IntPtr startup, out WindowsProcessInformation process);

    /// <summary>開始handleを所有した後に子processの主threadを再開します。</summary>
    /// <returns>元の停止回数、または失敗時uint.MaxValueです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint ResumeThread(IntPtr thread);

    /// <summary>起動の所有権確立に失敗した子processだけを回収します。</summary>
    /// <returns>終了要求の成功です。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr process, uint code);

    /// <summary>シェルの終了を待機します。</summary>
    /// <returns>待機結果です。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>終了したシェルの結果を取得します。</summary>
    /// <returns>取得成功です。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(IntPtr process, out int code);

    /// <summary>Win32から返されたプロセスとスレッドのハンドルを解放します。</summary>
    /// <returns>解放成功です。</returns>
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);
}
