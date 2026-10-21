using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>ブラウザーとCoreの暗号互換性と対象固定を検証します。</summary>
public sealed class DeviceLoginCipherTests
{
    /// <summary>Unicodeと空パスワードを実JSから.NETへ渡します。</summary>
    /// <returns>別ランタイム間の復号確認です。</returns>
    [Theory]
    [InlineData("試験専用😀password")]
    [InlineData("")]
    public async Task OpensTheBrowserCredentialWithoutPuttingPlaintextInTheRequestAsync(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        using DeviceLoginCipher cipher = new();
        string sid = "S-1-5-21-100-200-300-1001";
        DeviceChangeRequest request = Request();
        DeviceLoginCredential encrypted = await LoginTestCredential.SealInJavaScriptAsync(cipher.PublicKey, request, sid, password).ConfigureAwait(true);
        DeviceLoginChange change = new(true, sid, encrypted);
        char[] result = cipher.Open(request, change);
        Assert.Equal(password.ToCharArray(), result);
        Array.Clear(result);
        if (password.Length != 0)
        {
            Assert.DoesNotContain(password, DeviceSnapshots.Create(change).Data.GetRawText(), StringComparison.Ordinal);
        }
    }

    /// <summary>別PC・別Core起動実体・別操作・別ユーザーや改ざんを拒否します。</summary>
    [Theory]
    [InlineData("host")]
    [InlineData("instance")]
    [InlineData("operation")]
    [InlineData("user")]
    [InlineData("tamper")]
    [InlineData("restart")]
    public void RejectsCredentialReuseAndTampering(string scenario)
    {
        using DeviceLoginCipher cipher = new();
        using DeviceLoginCipher next = new();
        string sid = "S-1-5-21-100-200-300-1001";
        DeviceChangeRequest request = Request();
        DeviceLoginCredential encrypted = LoginTestCredential.Seal(cipher.PublicKey, request, sid, "synthetic-only");
        DeviceLoginChange change = new(true, sid, encrypted);
        switch (scenario)
        {
            case "host": request = request with { HostId = "other" }; break;
            case "instance": request = request with { InstanceId = Guid.NewGuid() }; break;
            case "operation": request = request with { OperationId = Guid.NewGuid() }; break;
            case "user": change = change with { UserSid = "S-1-5-21-100-200-300-1002" }; break;
            case "tamper":
                byte[] body = Convert.FromBase64String(encrypted.Ciphertext);
                body[0] ^= 1;
                change = change with { Credential = encrypted with { Ciphertext = Convert.ToBase64String(body) } };
                break;
            default:
                Assert.Throws<CryptographicException>(() => next.Open(request, change));
                return;
        }
        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(request, change));
    }

    /// <summary>この一回の固定対象を構成します。</summary>
    /// <returns>暗号の認証対象にもなる要求です。</returns>
    private static DeviceChangeRequest Request() => new(Guid.NewGuid(), "展示-PC", Guid.NewGuid(), EDeviceAction.Login,
        new(EDeviceArea.Login), new string('a', 64), DeviceSnapshots.Create(new DeviceLoginChange(false)).Data, true);
}
