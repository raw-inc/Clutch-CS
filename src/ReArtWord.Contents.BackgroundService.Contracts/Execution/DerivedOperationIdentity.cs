namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>保存した生成方式に従い、同じ操作のIDを回復時に変更しません。</summary>
public static class DerivedOperationIdentity
{
    /// <summary>SHA-256の先頭128ビットから用途固有のIDを作ります。</summary>
    /// <returns>旧方式と同じID、または時刻を表さないUUIDv8です。</returns>
    public static Guid FromHash(ReadOnlySpan<byte> hash, EDerivedIdFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hash.Length, 16);
        if (format == EDerivedIdFormat.Legacy)
        {
            return new Guid(hash[..16]);
        }
        ArgumentOutOfRangeException.ThrowIfNotEqual(format, EDerivedIdFormat.Uuid8);

        // ハッシュをそのままGUIDへ入れると、偶然UUIDv7の形式になり、先頭を発行日時として
        // 誤読されることがあります。SHA-256による名前由来のIDにはUUIDv8を使い、時刻付きIDと区別します。
        // https://www.rfc-editor.org/rfc/rfc9562.html#section-5.8
        // 方式は元操作の記録で選びます。結果が見つからないことを理由に旧方式から変更してはいけません。
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 15) | 128);
        bytes[8] = (byte)((bytes[8] & 63) | 128);
        return new Guid(bytes, bigEndian: true);
    }
}
