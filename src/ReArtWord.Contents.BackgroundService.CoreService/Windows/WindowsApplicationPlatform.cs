using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>登録アプリをログインユーザーのデスクトップへ起動し、別セッションからの通常終了を中継します。</summary>
internal sealed class WindowsApplicationPlatform : IProcessPlatform
{
    private readonly DefaultProcessPlatform standard = new();

    /// <summary>未ログイン中は起動意図を保存せず、ログイン後の同じ一回を待機できます。</summary>
    /// <returns>選択した起動セッションにユーザーが存在するかです。</returns>
    public bool IsLaunchUserAvailable(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!OperatingSystem.IsWindows() || definition.Launch?.Session != EProcessLaunchSession.Automatic)
        {
            return true;
        }
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        if (!current.IsSystem)
        {
            using Process owner = Process.GetCurrentProcess();
            return owner.SessionId != 0;
        }
        uint session = WindowsProcessNative.WTSGetActiveConsoleSessionId();
        bool available = WindowsProcessNative.WTSQueryUserToken(session, out SafeAccessTokenHandle token);
        token.Dispose();
        return available;
    }

    /// <summary>旧登録と明示Coreアカウントを維持し、通常のWindows表示はユーザーのセッションを使用します。</summary>
    /// <returns>監視側が所有する実プロセスです。</returns>
    public Process Start(ProcessDefinition definition)
    {
        if (OperatingSystem.IsWindows() && definition.Launch?.Session == EProcessLaunchSession.Automatic)
        {
            return StartInteractive(definition);
        }
        return standard.Start(definition);
    }

    /// <summary>異なるデスクトップのGUIも、実体を確認した一回限りの補助処理で終了要求します。</summary>
    /// <returns>実終了とは区別した要求送信結果です。</returns>
    public async ValueTask<bool> CloseWindowAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process target = Process.GetProcessById(identity.ProcessId);
            using Process current = Process.GetCurrentProcess();
            if (target.SessionId != current.SessionId)
            {
                try
                {
                    return await WindowsApplicationWindow.CloseAsync(identity, target.SessionId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
                {
                    // 通常終了の送信不能では対象を取り違えず、登録済みの待機・強制終了方針へ戻します。
                    return false;
                }
            }
        }
        return await standard.CloseWindowAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ログイン情報の取得不能をSYSTEMへの代替起動や結果不明へ変換しません。</summary>
    /// <returns>ユーザー権限で開始された実体です。</returns>
    [SupportedOSPlatform("windows")]
    private static Process StartInteractive(ProcessDefinition definition)
    {
        Exception? rejected = null;
        bool created = false;
        string reason = "process.interactiveUserUnavailable";
        try
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            using Process owner = Process.GetCurrentProcess();
            bool noWindow = definition.Launch?.Window == EProcessWindowMode.NoWindow;
            if (!current.IsSystem && owner.SessionId == 0)
            {
                throw new InvalidOperationException("The service account cannot obtain the interactive user token.");
            }
            if (!current.IsSystem && !WindowsInteractiveIdentity.IsAdministrator())
            {
                reason = "process.interactiveLaunchRejected";
                return WindowsProcessLaunch.Start(null, definition.ExecutablePath, definition.Arguments, definition.WorkingDirectory,
                    noWindow: noWindow, hideWindow: noWindow, createdCallback: () => created = true);
            }
            using SafeAccessTokenHandle token = WindowsInteractiveIdentity.UserToken(strictUserToken: true);
            reason = "process.interactiveLaunchRejected";
            return WindowsProcessLaunch.Start(token, definition.ExecutablePath, definition.Arguments, definition.WorkingDirectory,
                noWindow: noWindow, hideWindow: noWindow, createdCallback: () => created = true);
        }
        catch (Exception exception) when (!created && exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            rejected = exception;
        }
        throw new ProcessLaunchRejectedException(reason, rejected!);
    }
}
