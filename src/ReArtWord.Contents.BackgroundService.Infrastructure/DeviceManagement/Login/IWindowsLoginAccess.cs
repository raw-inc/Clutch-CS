using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>自動ログインの秘密値を返さず、OSへ限定した設定を渡します。</summary>
internal interface IWindowsLoginAccess
{
    /// <summary>秘密値を含まない状態を読みます。</summary>
    /// <returns>現在設定とユーザー候補です。</returns>
    public DeviceLoginState Read();

    /// <summary>選択したユーザーの資格情報を一度だけ検証します。</summary>
    /// <returns>対話ログインを許可された同じSIDかです。</returns>
    public bool Verify(DeviceLoginUser user, char[] password);

    /// <summary>次回ログオンの状態を明示します。</summary>
    public void SetEnabled(bool enabled);

    /// <summary>LSAに資格情報を保存します。nullは削除です。</summary>
    public void SetPassword(char[]? password);

    /// <summary>平文資格情報を残さず、対象と回数制限を設定します。</summary>
    public void SetUser(DeviceLoginUser user);

    /// <summary>旧方式の平文パスワードがあれば削除します。</summary>
    public void RemovePlaintextPassword();

    /// <summary>明示指定された場合だけ、現在適用中のログインメッセージを削除します。</summary>
    public void ClearLogonNotice();
}
