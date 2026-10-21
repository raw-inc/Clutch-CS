using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>JSONの数値精度とバイト列の区別を保ってPeer間へ運ぶ値です。</summary>
/// <param name="Kind">値の種類。</param>
/// <param name="Json">JSON値。</param>
/// <param name="Binary">バイト列。取得元の所有権を引き継がずコピーして構築します。</param>
public sealed record NodeValue(EValueKind Kind, JsonElement Json, ReadOnlyMemory<byte> Binary)
{
    /// <summary>Path・Int64・辞書を含む明示型です。JSON本体は通常の数値・文字列・配列・オブジェクトのまま運びます。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Values.ValueTypeDefinition? DataType
    {
        get; init;
    }

    /// <summary>入力済みの正常なnull値です。</summary>
    public static NodeValue Null
    {
        get;
    } = FromObject<object?>(null);

    /// <summary>JSONとして表現できる値を所有権を分けて取り込みます。</summary>
    /// <returns>独立したJSON値です。</returns>
    public static NodeValue FromObject<T>(T value)
    {
        return new NodeValue(EValueKind.Json, JsonSerializer.SerializeToElement(value), default);
    }

    /// <summary>JSON文書から独立した値を作成します。</summary>
    /// <returns>文書の寿命に依存しない値です。</returns>
    public static NodeValue FromJson(JsonElement value)
    {
        return new NodeValue(EValueKind.Json, value.Clone(), default);
    }

    /// <summary>バイト列をコピーして取り込みます。</summary>
    /// <returns>独立したバイト列です。</returns>
    public static NodeValue FromBytes(ReadOnlyMemory<byte> value)
    {
        return new NodeValue(EValueKind.Binary, Null.Json, value.ToArray());
    }

    /// <summary>分岐や保存先へ渡すために値をコピーします。</summary>
    /// <returns>元のバッファと文書に依存しない値です。</returns>
    public NodeValue Copy()
    {
        if (Kind == EValueKind.Binary)
        {
            return FromBytes(Binary);
        }
        return FromJson(Json) with { DataType = Values.PathTypeMetadata.Copy(DataType) };
    }

    /// <summary>JSON文字列を取得します。</summary>
    /// <returns>文字列の場合はその値、それ以外はnullです。</returns>
    public string? GetString()
    {
        if (Kind == EValueKind.Json && Json.ValueKind == JsonValueKind.String)
        {
            return Json.GetString();
        }
        return null;
    }

    /// <summary>保留・集約の上限に、本文だけでなく付随する型情報も計上します。</summary>
    /// <returns>JSONのUTF-8またはBinaryと、型情報のバイト数です。</returns>
    public long PayloadSize()
    {
        long size = Kind == EValueKind.Binary ? Binary.Length
                                              : System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(Json).Length;
        if (DataType is not null)
        {
            size += Serialization.JsonSize.Measure(DataType);
        }
        return size;
    }
}
