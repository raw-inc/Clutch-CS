using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>このCoreだけが復号できる一時鍵を所有し、暗号文を固定した要求へ結び付けます。</summary>
internal sealed class DeviceLoginCipher : IDisposable
{
    private readonly RSA rsa = RSA.Create(3072);
    private readonly Lock gate = new();
    private static readonly Lazy<DeviceLoginCipher> shared = new(() => new());

    /// <summary>Coreのプロセス寿命を所有者とし、秘密鍵をディスクへ保存しません。</summary>
    internal static DeviceLoginCipher Shared => shared.Value;

    /// <summary>公開鍵だけを画面へ渡します。</summary>
    internal DeviceLoginKey PublicKey
    {
        get
        {
            using (gate.EnterScope())
            {
                byte[] key = rsa.ExportSubjectPublicKeyInfo();
                return new(Convert.ToHexStringLower(SHA256.HashData(key)), Convert.ToBase64String(key));
            }
        }
    }

    /// <summary>Core再起動、別Host、別SID、別操作ID、改ざんされた暗号文を拒否します。</summary>
    /// <returns>呼出元が使用直後に消去するパスワード文字列バッファです。</returns>
    internal char[] Open(DeviceChangeRequest request, DeviceLoginChange change)
    {
        DeviceLoginCredential credential = change.Credential ?? throw new CryptographicException();
        credential.Validate();
        if (!string.Equals(PublicKey.Id, credential.KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException();
        }
        byte[] body = Convert.FromBase64String(credential.Ciphertext);
        byte[] key;
        using (gate.EnterScope())
        {
            key = rsa.Decrypt(Convert.FromBase64String(credential.WrappedKey), RSAEncryptionPadding.OaepSHA256);
        }
        byte[] plain = new byte[Math.Max(0, body.Length - 16)];
        try
        {
            if (key.Length != 32 || body.Length is < 16 or > 1040)
            {
                throw new CryptographicException();
            }
            using AesGcm aes = new(key, 16);
            aes.Decrypt(Convert.FromBase64String(credential.Nonce), body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain, Context(request, change.UserSid!));
            char[] password = new UTF8Encoding(false, true).GetChars(plain);
            if (256 < password.Length || password.Contains('\0'))
            {
                Array.Clear(password);
                throw new CryptographicException();
            }
            return password;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>暗号の認証対象を両端で同じ形式へ固定します。</summary>
    /// <returns>Host・起動実体・操作ID・SIDの改ざんを検出する追加データです。</returns>
    internal static byte[] Context(DeviceChangeRequest request, string sid)
    {
        if (request.HostId.Any(char.IsControl))
        {
            throw new ArgumentException("Device.InvalidRequest", nameof(request));
        }
        return Encoding.UTF8.GetBytes($"Contents.Login.v1\n{request.HostId}\n{request.InstanceId:D}\n{request.OperationId:D}\n{sid}");
    }

    /// <summary>試験で所有した鍵を解放します。共通鍵はプロセス終了時にOSが解放します。</summary>
    public void Dispose() => rsa.Dispose();
}
