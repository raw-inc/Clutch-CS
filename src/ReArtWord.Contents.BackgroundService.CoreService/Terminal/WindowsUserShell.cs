using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>PTYに接続した補助プロセスから、指定したユーザー権限でシェルを起動します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsUserShell
{
    /// <summary>親PTYのコンソールを継承し、ユーザー環境で起動したシェルの終了まで待ちます。</summary>
    /// <returns>シェルの終了コードです。</returns>
    internal static int Run(string shell, bool elevated, string userSid, string readyPipe, bool strictUserToken)
    {
        using NamedPipeClientStream pipe = new(".", readyPipe, PipeDirection.Out);
        pipe.Connect(10000);
        try
        {
            return RunUser(shell, elevated, userSid, pipe, strictUserToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            int result = 0;
            if (exception is Win32Exception native)
            {
                result = -native.NativeErrorCode;
            }
            Report(pipe, result);
            return 1;
        }
    }

    /// <summary>要求受付時と同じSIDを確認し、ユーザー環境をPTYへ接続します。</summary>
    /// <returns>起動したシェルの終了コードです。</returns>
    private static int RunUser(string shell, bool elevated, string userSid, NamedPipeClientStream ready, bool strictUserToken)
    {
        using SafeAccessTokenHandle token = WindowsInteractiveIdentity.UserToken(elevated, strictUserToken);
        using WindowsIdentity user = new(token.DangerousGetHandle());
        if (!string.Equals(user.User?.Value, userSid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The interactive user changed before the terminal started.");
        }
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem)
        {
            // コンソールを共有するため、利用者トークンをPTY補助プロセスのセッションへ置きます。
            using Process current = Process.GetCurrentProcess();
            int session = current.SessionId;
            if (!WindowsProcessNative.SetTokenInformation(token, 12, ref session, sizeof(int)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        if (!WindowsProcessNative.CreateEnvironmentBlock(out IntPtr environment, token, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            char[] profile = new char[32768];
            int length = profile.Length;
            if (!WindowsProcessNative.GetUserProfileDirectoryUnsafe(token, profile, ref length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return Start(token, shell, new string(profile, 0, length - 1), environment, ready, identity.IsSystem);
        }
        finally
        {
            _ = WindowsProcessNative.DestroyEnvironmentBlock(environment);
        }
    }

    /// <summary>Win32 STARTUPINFOをゼロ初期化し、既存のコンソールとジョブを継承します。</summary>
    /// <returns>待機したプロセスの終了コードです。</returns>
    private static int Start(SafeAccessTokenHandle token, string shell, string directory, IntPtr environment, NamedPipeClientStream ready, bool serviceDesktop)
    {
        int size = 68;
        if (Environment.Is64BitProcess)
        {
            size = 104;
        }
        IntPtr startup = Marshal.AllocHGlobal(size + sizeof(char));
        try
        {
            Marshal.Copy(new byte[size + sizeof(char)], 0, startup, size + sizeof(char));
            Marshal.WriteInt32(startup, size);

            if (serviceDesktop)
            {
                // SYSTEMでは利用者専用の非対話デスクトップを選び、サービスのDACLを変更しません。
                Marshal.WriteIntPtr(startup, 2 * IntPtr.Size, startup + size);
            }
            if (!WindowsProcessNative.CreateProcessAsUserW(token, shell, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, false, 0x400, environment, directory, startup, out WindowsProcessInformation process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                Report(ready, checked((int)process.ProcessId));
                if (WindowsProcessNative.WaitForSingleObject(process.Process, uint.MaxValue) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (!WindowsProcessNative.GetExitCodeProcess(process.Process, out int code))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return code;
            }
            finally
            {
                _ = WindowsProcessNative.CloseHandle(process.Thread);
                _ = WindowsProcessNative.CloseHandle(process.Process);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(startup);
        }
    }

    /// <summary>シェル作成の成否をコンソール出力と混ぜずに通知します。</summary>
    private static void Report(NamedPipeClientStream pipe, int result)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, result);
        pipe.Write(bytes);
        pipe.Flush();
    }
}
