using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>ユーザーのデスクトップ内にCore自身の補助モードを起動します。</summary>
[SupportedOSPlatform("windows")]
internal static class DesktopWorkerProcess
{
    /// <summary>Coreと同じ実行主体だけが接続できる一回限りのpipeを作成します。</summary>
    /// <returns>呼出元が所有するpipeです。</returns>
    internal static NamedPipeServerStream CreatePipe(string name)
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(current.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetAccessRuleProtection(true, false);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
    }

    /// <summary>同じユーザーの別プロセスによるpipe接続を拒否します。</summary>
    internal static void Verify(NamedPipeServerStream pipe, Process worker)
    {
        if (!WindowsProcessNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) || processId != worker.Id)
        {
            throw new IOException("The desktop pipe belongs to another process.");
        }
    }

    /// <summary>Coreの権限を維持し、Systemサービスではログオン中のコンソールへ起動します。</summary>
    /// <returns>呼出元が終了・破棄する補助プロセスです。</returns>
    internal static Process Start(string pipeName, string encoder)
        => StartCore(["--desktop-worker", pipeName, encoder]);

    /// <summary>表示設定専用の固定補助モードをコンソール内へ起動します。</summary>
    /// <returns>呼出元が寿命を所有する補助プロセスです。</returns>
    internal static Process StartDisplay(string pipeName) => StartCore(["--device-display-worker", pipeName]);

    /// <summary>登録アプリケーションのデスクトップで、一件の通常終了要求だけを処理します。</summary>
    /// <returns>要求元が回収する固定補助モードです。</returns>
    internal static Process StartApplicationClose(string pipeName, int session) => StartCore(["--application-close-worker", pipeName], checked((uint)session));

    /// <summary>同じ実行主体を維持し、Core自身の限定した補助モードだけを起動します。</summary>
    /// <returns>開始した補助プロセスです。</returns>
    private static Process StartCore(IReadOnlyList<string> workerArguments, uint? targetSession = null)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Core executable is unavailable.");
        List<string> arguments = [];
        string dotnet = "dotnet";
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), dotnet, StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(typeof(DesktopWorkerProcess).Assembly.Location);
        }
        arguments.AddRange(workerArguments);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
        {
            ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            return Process.Start(info) ?? throw new IOException("Desktop worker could not start.");
        }
        uint active = targetSession ?? WindowsProcessNative.WTSGetActiveConsoleSessionId();
        if (active == uint.MaxValue)
        {
            throw new InvalidOperationException("No interactive console user is available for remote desktop.");
        }
        if (!WindowsProcessNative.WTSQueryUserToken(active, out SafeAccessTokenHandle user))
        {
            user.Dispose();
            throw new InvalidOperationException("No interactive console user is available for remote desktop.");
        }
        user.Dispose();
        if (!WindowsProcessNative.DuplicateTokenEx(identity.AccessToken, 0x02000000, IntPtr.Zero, 2, 1, out SafeAccessTokenHandle token))
        {
            int error = Marshal.GetLastWin32Error();
            token.Dispose();
            throw new Win32Exception(error);
        }
        using SafeAccessTokenHandle ownedToken = token;
        int session = checked((int)active);
        if (!WindowsProcessNative.SetTokenInformation(token, 12, ref session, sizeof(int)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return StartAsUser(token, executable, arguments);
    }

    /// <summary>対話desktopを明示し、セッション0にトークンを移しません。</summary>
    /// <returns>開始したプロセスです。</returns>
    private static Process StartAsUser(SafeAccessTokenHandle? token, string executable, IReadOnlyList<string> arguments, string desktopName = "default")
        => WindowsProcessLaunch.Start(token, executable, arguments, AppContext.BaseDirectory, desktopName);

    /// <summary>標準handleを継承せず、System workerの取得先desktopを明示します。</summary>
    /// <returns>呼出元が終了・破棄するencoderです。</returns>
    internal static Process StartCapture(ProcessStartInfo info, string desktopName)
        => StartAsUser(null, info.FileName, info.ArgumentList.ToArray(), desktopName);

}
