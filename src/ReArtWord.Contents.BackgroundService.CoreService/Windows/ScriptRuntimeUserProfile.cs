using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>実行環境の導入先をサービスのホームではなく、対象PCのログイン利用者へ結び付けます。</summary>
internal static class ScriptRuntimeUserProfile
{
    /// <summary>対話実行では本人、Windowsサービスでは現在のコンソール利用者を取得します。</summary>
    /// <returns>ホームと利用者のPATHです。未ログイン時は代替先を返しません。</returns>
    internal static ScriptRuntimeUser Read()
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindows();
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string rootUser = "root";
        if (string.IsNullOrEmpty(home) || string.Equals(Environment.UserName, rootUser, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("script.runtime.userUnavailable");
        }
        return new(Environment.UserName, Environment.UserName, home, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>OSから返されたトークンと環境ブロックの所有権を同じ呼出内で閉じます。</summary>
    /// <returns>サービスアカウントへ置き換えない利用者情報です。</returns>
    [SupportedOSPlatform("windows")]
    private static ScriptRuntimeUser ReadWindows()
    {
        using Process process = Process.GetCurrentProcess();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SafeAccessTokenHandle token;
        if (process.SessionId == 0)
        {
            if (!WindowsProcessNative.WTSQueryUserToken(WindowsProcessNative.WTSGetActiveConsoleSessionId(), out token))
            {
                int error = Marshal.GetLastWin32Error();
                token.Dispose();
                throw new Win32Exception(error);
            }
        }
        else
        {
            if (!WindowsProcessNative.DuplicateTokenEx(identity.AccessToken, 0x0008, IntPtr.Zero, 2, 2, out token))
            {
                int error = Marshal.GetLastWin32Error();
                token.Dispose();
                throw new Win32Exception(error);
            }
        }
        using (token)
        {
            using WindowsIdentity user = new(token.DangerousGetHandle());
            if (user.IsSystem || user.User is null)
            {
                throw new InvalidOperationException("script.runtime.userUnavailable");
            }
            char[] profile = new char[32768];
            int length = profile.Length;
            if (!WindowsProcessNative.GetUserProfileDirectoryUnsafe(token, profile, ref length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!WindowsProcessNative.CreateEnvironmentBlock(out IntPtr environment, token, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                return new(user.User.Value, user.Name, new string(profile, 0, length - 1), SearchPath(environment));
            }
            finally
            {
                WindowsProcessNative.DestroyEnvironmentBlock(environment);
            }
        }
    }

    /// <summary>有限のUTF-16環境ブロックからPATHだけを取り出します。</summary>
    /// <returns>利用者とシステムを合成した探索パスです。</returns>
    private static string? SearchPath(IntPtr environment)
    {
        string prefix = "PATH=";
        for (int offset = 0; offset < 1048576;)
        {
            string entry = Marshal.PtrToStringUni(environment + offset) ?? string.Empty;
            if (entry.Length == 0)
            {
                return null;
            }
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry[prefix.Length..];
            }
            offset += checked((entry.Length + 1) * sizeof(char));
        }
        throw new InvalidOperationException("script.runtime.userEnvironmentInvalid");
    }
}
