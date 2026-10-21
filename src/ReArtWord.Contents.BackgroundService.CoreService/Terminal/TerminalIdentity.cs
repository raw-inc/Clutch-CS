using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Porta.Pty;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>要求された権限を確認し、PTY内で起動する実行ファイルを選びます。</summary>
internal static partial class TerminalIdentity
{
    /// <summary>PTY補助プロセス専用の起動引数です。</summary>
    internal static string UserShellArgument => "--terminal-user-shell";
    internal static string AdministratorShellArgument => "--terminal-administrator-shell";

    /// <summary>通常ユーザーと管理者を暗黙に置き換えず、実行可能な権限だけ許可します。</summary>
    /// <returns>未ログオン時のSYSTEM起動として構成したかです。</returns>
    internal static bool Configure(PtyOptions options, bool elevated, string? readyPipe = null, bool strictUserToken = false)
    {
        if (OperatingSystem.IsWindows())
        {
            if (elevated)
            {
                if (!WindowsInteractiveIdentity.IsAdministrator())
                {
                    throw new InvalidOperationException("Administrator sessions require the Core service to run as an administrator.");
                }
            }
            if (!WindowsInteractiveIdentity.IsAdministrator())
            {
                return false;
            }
            using Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token = WindowsInteractiveIdentity.UserToken(elevated, strictUserToken);
            using WindowsIdentity user = new(token.DangerousGetHandle());
            string mode = UserShellArgument;
            if (elevated)
            {
                mode = AdministratorShellArgument;
            }
            string shell = options.App;
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The Core executable path is unavailable.");
            options.App = executable;
            string policy = "compatible";
            if (strictUserToken)
            {
                policy = "strict";
            }
            options.CommandLine = [mode, shell, user.User!.Value, readyPipe ?? string.Empty, policy];
            string dotnet = "dotnet";
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), dotnet, StringComparison.OrdinalIgnoreCase))
            {
                options.CommandLine = [typeof(TerminalIdentity).Assembly.Location, mode, shell, user.User!.Value, readyPipe ?? string.Empty, policy];
            }
            return user.IsSystem;
        }
        if (elevated != (GetEffectiveUserId() == 0))
        {
            throw new InvalidOperationException("The requested terminal privilege is unavailable for this Core service account.");
        }
        return false;
    }

    /// <summary>UACを呼び出さず、現在の実行主体で使用可能なモードを返します。</summary>
    /// <returns>UIの活性条件です。作成時にも同じ条件を再検査します。</returns>
    internal static TerminalCapabilities Capabilities(bool strictUserToken = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            bool root = GetEffectiveUserId() == 0;
            return new(!root, root);
        }
        if (!WindowsInteractiveIdentity.IsAdministrator())
        {
            return new(true, false, AdministratorReason: "CoreAdministratorRequired");
        }
        bool user = Available(false, out _), administrator = Available(true, out bool system);
        string? userReason = null, administratorReason = null;
        if (!user)
        {
            userReason = "UserTokenUnavailable";
        }
        if (!administrator)
        {
            administratorReason = "AdministratorTokenUnavailable";
        }
        return new(user, administrator, userReason, administratorReason, system);

        bool Available(bool elevated, out bool system)
        {
            system = false;
            try
            {
                using Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token = WindowsInteractiveIdentity.UserToken(elevated, strictUserToken);
                using WindowsIdentity identity = new(token.DangerousGetHandle());
                system = identity.IsSystem;
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Unixの実効ユーザーIDを取得します。</summary>
    /// <returns>rootでは0です。</returns>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint GetEffectiveUserId();
}
