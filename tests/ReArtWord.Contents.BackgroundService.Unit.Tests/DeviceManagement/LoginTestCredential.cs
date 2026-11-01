using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実アカウントを使わず、暗号の境界へ合成資格情報を渡します。</summary>
internal static class LoginTestCredential
{
    /// <summary>試験用公開鍵へ合成値を暗号化します。</summary>
    /// <returns>ブラウザーと同形式の資格情報です。</returns>
    internal static DeviceLoginCredential Seal(DeviceLoginKey key, DeviceChangeRequest request, string sid, string password)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.Spki), out _);
        byte[] aesKey = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12), plain = Encoding.UTF8.GetBytes(password);
        byte[] body = new byte[plain.Length + 16];
        using AesGcm aes = new(aesKey, 16);
        aes.Encrypt(nonce, plain, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), DeviceLoginCipher.Context(request, sid));
        return new(key.Id, Convert.ToBase64String(rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256)), Convert.ToBase64String(nonce), Convert.ToBase64String(body));
    }

    /// <summary>実ブラウザーと同じJS関数をNodeで呼び、平文を引数やファイルに保存しません。</summary>
    /// <returns>別ランタイムが生成した暗号文です。</returns>
    internal static async Task<DeviceLoginCredential> SealInJavaScriptAsync(DeviceLoginKey key, DeviceChangeRequest request, string sid, string password, CancellationToken cancellationToken = default)
    {
        string script = Path.GetFullPath("../../../../../tests/ReArtWord.Contents.BackgroundService.Client.Tests/Client/login-credential.fixture.mjs", AppContext.BaseDirectory);
        ProcessStartInfo start = new("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        using Process process = Process.Start(start) ?? throw new IOException("Credential fixture did not start.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { key, password, target = new { request.HostId, request.InstanceId }, request.OperationId, userSid = sid }, ContractJson.Options).AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await ProcessCompletion.WaitAsync(process, timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException(error);
            }
            return JsonSerializer.Deserialize<DeviceLoginCredential>(output, ContractJson.Options)!;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
