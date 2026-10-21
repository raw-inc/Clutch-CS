using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>保存内容の欠落と破損を読み取り前に検出します。</summary>
internal static class RepositoryCodec
{
    /// <summary>検査値を付けた保存形式へ変換します。</summary>
    /// <returns>検査値とJSON本文。</returns>
    public static byte[] Encode(RepositoryDocument document)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new RepositoryEnvelope(ContractJson.Hash(document), document));
    }

    /// <summary>破損を正常な空保存先へ置き換えず拒否します。</summary>
    /// <returns>検証済み文書。</returns>
    public static RepositoryDocument Decode(byte[] bytes)
    {
        RepositoryEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<RepositoryEnvelope>(bytes);
        }
        catch (JsonException)
        {
            envelope = null;
        }
        if (envelope?.Data is null || !string.Equals(envelope.Hash, ContractJson.Hash(envelope.Data), StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存内容の整合性を確認できません。");
        }
        return envelope.Data;
    }
}
