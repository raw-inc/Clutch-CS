using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>秘密値の復号と認証確認を経て、自動ログインを最後に有効化します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLoginSettings(IWindowsLoginAccess access, DeviceLoginCipher cipher)
{
    /// <summary>同じCoreの一時鍵とWindowsのLSAを使用します。</summary>
    internal WindowsLoginSettings() : this(new WindowsLoginAccess(), DeviceLoginCipher.Shared)
    {
    }

    /// <summary>公開鍵だけを現在値へ追加します。</summary>
    /// <returns>パスワード本文を含まない内容版です。</returns>
    internal DeviceSnapshot Read() => DeviceSnapshots.Create(access.Read() with { EncryptionKey = cipher.PublicKey });

    /// <summary>未確認資格情報ではOSを書き換えず、有効化を最後に行います。</summary>
    /// <returns>秘密値を含まない開始前拒否または設定結果です。</returns>
    internal DeviceChangeResult Apply(DeviceChangeRequest request, DeviceSnapshot before)
    {
        DeviceLoginChange change = DeviceSnapshots.Read<DeviceLoginChange>(request.Change);
        DeviceSnapshot latest = Read();
        if (!string.Equals(latest.Revision, before.Revision, StringComparison.Ordinal))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
        }
        DeviceLoginState state = DeviceSnapshots.Read<DeviceLoginState>(latest.Data);
        char[]? password = null;
        List<string> steps = [];
        try
        {
            change.Validate(state);
            DeviceLoginUser? user = null;
            if (change.Enabled)
            {
                user = state.Users.Single(item => string.Equals(item.Sid, change.UserSid, StringComparison.Ordinal));
                password = cipher.Open(request, change);
                if (!access.Verify(user, password))
                {
                    return new(request.OperationId, EOutcome.NotExecuted, "Device.LoginCredentialsRejected");
                }
                if (!string.Equals(Read().Revision, latest.Revision, StringComparison.Ordinal))
                {
                    return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
                }
            }
            access.SetEnabled(false);
            steps.Add("login.disabled");
            access.SetPassword(password);
            steps.Add("login.secretUpdated");
            access.RemovePlaintextPassword();
            steps.Add("login.plaintextRemoved");
            if (user is not null)
            {
                access.SetUser(user);
                steps.Add("login.userSaved");
                if (change.ClearLogonNotice)
                {
                    access.ClearLogonNotice();
                    if (access.Read().Restrictions.Count != 0)
                    {
                        return new(request.OperationId, EOutcome.Failed, "Device.LoginNoticeClearFailed", CompletedSteps: steps);
                    }
                    steps.Add("login.noticeCleared");
                }
                access.SetEnabled(true);
                steps.Add("login.enabled");
            }
            return new(request.OperationId, EOutcome.Succeeded, CompletedSteps: steps);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or DecoderFallbackException)
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.LoginCredentialInvalid");
        }
        catch (ArgumentException)
        {
            EOutcome outcome = steps.Count == 0 ? EOutcome.NotExecuted
                                                : EOutcome.Failed;
            return new(request.OperationId, outcome, "Device.LoginUnavailable", CompletedSteps: steps);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(request.OperationId, EOutcome.Failed, "Device.LoginChangeFailed", CompletedSteps: steps);
        }
        finally
        {
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }
}
