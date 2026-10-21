using System.Security.Cryptography;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>利用者の操作IDを管理輸送の元要求IDへ対応させます。</summary>
public static class ManagementCommandIdentity
{
    /// <summary>クライアントの結果照会でも同じ要求を参照します。</summary>
    /// <returns>操作名と元IDで固定した管理IDです。</returns>
    public static Guid Create(Guid id, string operation) => new(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N") + "\0" + operation)).AsSpan(0, 16));
}
