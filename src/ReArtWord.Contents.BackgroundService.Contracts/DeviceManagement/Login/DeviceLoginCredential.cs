namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

/// <summary>ブラウザーで暗号化済みの資格情報。平文パスワードは契約に含めません。</summary>
public sealed record DeviceLoginCredential(string KeyId, string WrappedKey, string Nonce, string Ciphertext)
{
    /// <summary>暗号文の型と有限の長さを保存前に確認します。</summary>
    public void Validate()
    {
        if (KeyId.Length != 64 || !KeyId.All(Uri.IsHexDigit) || WrappedKey.Length != 512 || Nonce.Length != 16 || Ciphertext.Length is < 24 or > 1392)
        {
            throw new ArgumentException("Device.LoginCredentialInvalid");
        }
        foreach (string value in new[] { WrappedKey, Nonce, Ciphertext })
        {
            if (!value.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '='))
            {
                throw new ArgumentException("Device.LoginCredentialInvalid");
            }
        }
    }
}
