namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

/// <summary>ログイン対象と、この一回の操作に暗号で結び付けた資格情報です。</summary>
public sealed record DeviceLoginChange(bool Enabled, string? UserSid = null, DeviceLoginCredential? Credential = null, bool ClearLogonNotice = false)
{
    /// <summary>有効化時だけ対象と資格情報を要求し、無効化へ秘密値を混ぜません。</summary>
    public void Validate(DeviceLoginState? current = null)
    {
        if (!Enabled)
        {
            if (UserSid is not null || Credential is not null || ClearLogonNotice)
            {
                throw new ArgumentException("Device.InvalidRequest");
            }
            return;
        }
        string prefix = "S-1-";
        if (UserSid is null || UserSid.Length is < 8 or > 184 || !UserSid.StartsWith(prefix, StringComparison.Ordinal)
            || UserSid.Any(character => !char.IsAsciiDigit(character) && character is not ('S' or '-')) || Credential is null)
        {
            throw new ArgumentException("Device.LoginSelectUser");
        }
        Credential.Validate();
        if (current is not null && (current.HasBlockingPolicy() || !current.Users.Any(user => user.Selectable && string.Equals(user.Sid, UserSid, StringComparison.Ordinal))))
        {
            throw new ArgumentException("Device.LoginUnavailable");
        }
    }

    /// <summary>選択したユーザーと秘密値の保存先まで確認します。</summary>
    /// <returns>要求された自動ログイン設定を確認できたかです。</returns>
    public bool Matches(DeviceLoginState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (Enabled)
        {
            return current.Enabled == true && string.Equals(current.UserSid, UserSid, StringComparison.Ordinal)
                && current.ProtectedPasswordPresent == true && !current.PlaintextPasswordPresent && current.RemainingLogons is null
                && !current.HasBlockingPolicy() && (!ClearLogonNotice || current.Restrictions.Count == 0);
        }
        return current.Enabled == false && current.ProtectedPasswordPresent == false && !current.PlaintextPasswordPresent;
    }
}
