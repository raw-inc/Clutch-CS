using System.Security.Cryptography;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>利用者の操作IDを管理輸送の元要求IDへ対応させます。</summary>
public static class ManagementCommandIdentity
{
    /// <summary>クライアントの結果照会でも同じ要求を参照します。</summary>
    /// <returns>操作名と元IDで固定した管理IDです。</returns>
    public static Guid Create(Guid id, string operation) => Create(id, operation, EDerivedIdFormat.Legacy);

    /// <summary>元の要求に固定した生成方式を使い、再送で管理IDを変更しません。</summary>
    /// <returns>操作名・元ID・生成方式に対応する管理IDです。</returns>
    public static Guid Create(Guid id, string operation, EDerivedIdFormat format)
        => DerivedOperationIdentity.FromHash(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N") + "\0" + operation)), format);
}
