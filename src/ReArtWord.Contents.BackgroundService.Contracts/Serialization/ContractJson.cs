using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Serialization;

/// <summary>公開契約のJSON表現と内容識別を統一します。</summary>
public static class ContractJson
{
    private static readonly ArrayPool<NamedProperty> propertyPool = ArrayPool<NamedProperty>.Create(1024, 2);

    /// <summary>列挙値をcamelCaseで表す読み取り専用の共通設定です。</summary>
    public static JsonSerializerOptions Options
    {
        get;
    } = CreateOptions();

    /// <summary>公開契約の項目名と列挙値を保ったJSONを、ノードの値として渡します。</summary>
    /// <returns>契約と同じcamelCaseのJSON値。</returns>
    public static Nodes.NodeValue ToValue<T>(T value) => Nodes.NodeValue.FromJson(JsonSerializer.SerializeToElement(value, Options));

    /// <summary>可変な入力バッファ・コレクションから所有権を分離します。</summary>
    /// <returns>JSON契約を維持した独立した値です。</returns>
    public static T Copy<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using JsonBuffer buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = Options.MaxDepth, Encoder = Options.Encoder }))
        {
            JsonSerializer.Serialize(writer, value, Options);
        }
        return JsonSerializer.Deserialize<T>(buffer.Written, Options)!;
    }

    /// <summary>オブジェクト項目順に依存しない内容識別子を作成します。</summary>
    /// <returns>SHA-256の小文字16進表記です。</returns>
    public static string Hash<T>(T value)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using HashBufferWriter buffer = new(hash);
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = Options.MaxDepth }))
        {
            WriteCanonical(writer, value);
        }
        Span<byte> digest = stackalloc byte[32];
        _ = hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>内容識別と同じ項目順・数値・エスケープ規則で、呼出元のJSON出力へ書き込みます。</summary>
    public static void WriteCanonical<T>(Utf8JsonWriter writer, T value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is JsonElement element)
        {
            WriteCanonical(writer, element);
        }
        else
        {
            using JsonDocument document = JsonSerializer.SerializeToDocument(value, Options);
            WriteCanonical(writer, document.RootElement);
        }
    }

    /// <summary>オブジェクト項目を名前順にして構造・配列順・数値を保持します。</summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    /// <summary>小さいオブジェクトはスタック内、大きいものは借用配列で安定した名前順にします。</summary>
    private static void WriteObject(Utf8JsonWriter writer, JsonElement value)
    {
        int count = value.GetPropertyCount();
        PropertyBuffer local = default;
        NamedProperty[]? rented = null;
        Span<NamedProperty> properties = local;
        if (properties.Length < count)
        {
            rented = propertyPool.Rent(count);
            properties = rented;
        }
        properties = properties[..count];
        try
        {
            int index = 0;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                properties[index] = new(property, property.Name, index);
                index++;
            }
            properties.Sort();
            writer.WriteStartObject();
            foreach (NamedProperty property in properties)
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Property.Value);
            }
            writer.WriteEndObject();
        }
        finally
        {
            if (rented is not null)
            {
                propertyPool.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>名前の復号は一回とし、同名項目は元の出現順を維持します。</summary>
    private readonly record struct NamedProperty(JsonProperty Property, string Name, int Position) : IComparable<NamedProperty>
    {
        /// <summary>序数の名前順と元の位置で比較します。</summary>
        /// <returns>重複名を逆転させない順序です。</returns>
        public int CompareTo(NamedProperty other)
        {
            int order = string.CompareOrdinal(Name, other.Name);
            if (order != 0)
            {
                return order;
            }
            return Position.CompareTo(other.Position);
        }
    }

    /// <summary>通常の契約オブジェクトでソート用の配列を確保しません。</summary>
    [InlineArray(8)]
    private struct PropertyBuffer
    {
        private NamedProperty first;

        /// <summary>JSONの本文を含めず、一時領域の容量だけを表示します。</summary>
        /// <returns>診断用の領域名です。</returns>
        public override string ToString() => "Canonical properties (capacity: 8)";
    }

    /// <summary>契約の読み取り規則を作成し固定します。</summary>
    /// <returns>変更できないJSON設定です。</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
