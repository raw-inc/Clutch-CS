using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>同じ発行元で実際に退役を確認したIDだけを、欠番を含めずにまとめます。</summary>
/// <param name="Prefix">GUIDの先頭96ビットを表す、小文字24桁の16進表記です。</param>
/// <param name="First">退役を確認した連続区間の先頭です。</param>
/// <param name="Last">退役を確認した連続区間の末尾です。</param>
/// <param name="Shared">共有操作として同期するかです。</param>
/// <param name="Scope">操作結果ならnull、受付キーなら元の保存スコープです。</param>
/// <param name="KeyPrefix">受付キーのowner:などの接頭辞です。</param>
/// <param name="KeyFormat">受付キーのGUID表記です。大文字指定では16進文字も大文字に戻します。</param>
public sealed record RetiredIdRange(string Prefix, uint First, uint Last, bool Shared, string? Scope = null, string KeyPrefix = "", string KeyFormat = "n")
{
    private static readonly string[] keyFormats = ["n", "d", "b", "p", "N", "D", "B", "P"];

    /// <summary>結果と受付キー、共有とローカルの退役証拠を混ぜない索引キーです。</summary>
    [JsonIgnore]
    internal string Bucket
    {
        get
        {
            string visibility = Shared ? "s:"
                                       : "l:";
            string key = visibility + Prefix;
            return Scope is null ? key
                                 : key + ":" + DurableRepository.GetAddress(Scope, KeyPrefix + "\0" + KeyFormat);
        }
    }

    /// <summary>操作IDの実値から一点の区間を作ります。IDの版を日時へ読み替えません。</summary>
    /// <returns>指定したIDだけを含む区間です。</returns>
    internal static RetiredIdRange Operation(Guid id, bool shared)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = id.TryWriteBytes(bytes, bigEndian: true, out _);
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
        return new(id.ToString("N")[..24], sequence, sequence, shared);
    }

    /// <summary>元の表記へ戻せるGUIDキーだけを区間化し、それ以外は既存の個別索引へ残します。</summary>
    /// <returns>表記を保てる受付キーなら一点の区間、それ以外はnullです。</returns>
    internal static RetiredIdRange? Key(string scope, string key)
    {
        string owner = "owner:";
        string prefix = string.Empty;
        if (key.StartsWith(owner, StringComparison.Ordinal))
        {
            prefix = owner;
        }
        string value = key[prefix.Length..];
        if (!Guid.TryParse(value, out Guid id))
        {
            return null;
        }
        foreach (string format in new[] { "n", "d", "b", "p" })
        {
            string canonical = id.ToString(format);
            if (string.Equals(value, canonical, StringComparison.Ordinal))
            {
                return Operation(id, DurableRepository.IsShared(scope)) with { Scope = scope, KeyPrefix = prefix, KeyFormat = format };
            }
            if (string.Equals(value, canonical.ToUpperInvariant(), StringComparison.Ordinal))
            {
                return Operation(id, DurableRepository.IsShared(scope)) with { Scope = scope, KeyPrefix = prefix, KeyFormat = format.ToUpperInvariant() };
            }
        }
        return null;
    }

    /// <summary>不正な区間や、受付キーの表記を変えてしまうメタデータを拒否します。</summary>
    public void Validate()
    {
        int prefixLength = 24;
        string suffix = "00000000", format = "N", defaultKeyFormat = "n", owner = "owner:";
        bool validPrefix = Guid.TryParseExact(Prefix + suffix, format, out Guid first);
        if (Prefix is null || Prefix.Length != prefixLength || !validPrefix
            || !string.Equals(Prefix, first.ToString(format)[..prefixLength], StringComparison.Ordinal) || Last < First
            || !keyFormats.Contains(KeyFormat, StringComparer.Ordinal) || !(string.IsNullOrEmpty(KeyPrefix) || string.Equals(KeyPrefix, owner, StringComparison.Ordinal))
            || Scope is null && (!string.IsNullOrEmpty(KeyPrefix) || !string.Equals(KeyFormat, defaultKeyFormat, StringComparison.Ordinal))
            || Scope is not null && (string.IsNullOrWhiteSpace(Scope) || !RepositoryHistoryGuard.IsSingleUse(Scope) || Shared != DurableRepository.IsShared(Scope)))
        {
            throw new InvalidDataException("退役済みIDの区間を確認できません。");
        }
    }

    /// <summary>旧形式の相手へ渡す場合も、元のIDを再発行せず復元します。</summary>
    /// <returns>指定した連番の元IDです。</returns>
    internal Guid Identity(uint sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, First);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, Last);
        return Guid.ParseExact(Prefix + sequence.ToString("x8", CultureInfo.InvariantCulture), "N");
    }

    /// <summary>旧形式への書出しで、ハイフンや大文字・小文字を含む元の受付キーを維持します。</summary>
    /// <returns>指定した連番の受付キーです。</returns>
    internal string KeyAt(uint sequence)
    {
        string key = Identity(sequence).ToString(KeyFormat);
        if (char.IsUpper(KeyFormat[0]))
        {
            key = key.ToUpperInvariant();
        }
        return KeyPrefix + key;
    }
}
