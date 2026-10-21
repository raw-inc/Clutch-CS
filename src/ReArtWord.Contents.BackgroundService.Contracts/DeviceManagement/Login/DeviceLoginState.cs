namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

/// <summary>秘密値を含まない自動ログイン設定とローカルユーザー候補です。</summary>
public sealed record DeviceLoginState(bool? Enabled, string UserName, string Domain, string? UserSid, bool? ProtectedPasswordPresent,
    bool PlaintextPasswordPresent, int? RemainingLogons, IReadOnlyList<DeviceLoginUser> Users, IReadOnlyList<string> Restrictions, DeviceLoginKey? EncryptionKey = null)
{
    /// <summary>ログインメッセージは保存を妨げず、その他の競合ポリシーは設定を拒否します。</summary>
    /// <returns>アカウント設定を禁止するポリシーがあるかです。</returns>
    public bool HasBlockingPolicy()
    {
        string notice = "Device.LoginNoticePolicy";
        return Restrictions.Any(reason => !string.Equals(reason, notice, StringComparison.Ordinal));
    }
}
