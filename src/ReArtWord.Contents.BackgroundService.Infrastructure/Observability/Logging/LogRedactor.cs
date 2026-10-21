using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>登録済み秘密値と個人情報をログの各境界で伏せます。</summary>
internal sealed class LogRedactor
{
    private readonly SecretStore secrets;
    private readonly Lock gate = new();
    private readonly HashSet<string> known = new(StringComparer.Ordinal);

    /// <summary>現在値を一括取得する保管先を使用します。</summary>
    internal LogRedactor(SecretStore secrets)
    {
        this.secrets = secrets;
    }

    /// <summary>秘密変更直後にも最新の伏字対象を取得します。</summary>
    /// <returns>この境界に固定する伏字対象です。</returns>
    internal async Task<IReadOnlyList<string>> ReadPatternsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> current = await secrets.ReadRedactionValuesAsync(cancellationToken).ConfigureAwait(false);
        using (gate.EnterScope())
        {
            foreach (string value in current)
            {
                known.Add(value);
                known.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                known.Add(Uri.EscapeDataString(value));
                known.Add(JsonEncodedText.Encode(value).ToString());
            }
            return known.OrderByDescending(value => value.Length).ToArray();
        }
    }

    /// <summary>秘密は全レベル、個人情報は非DEBUGで伏せます。</summary>
    /// <returns>呼出側入力を変更しない保護済みログです。</returns>
    internal static WorkflowLogEntry Redact(WorkflowLogEntry entry, IReadOnlyList<string> patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Level);
        ArgumentNullException.ThrowIfNull(entry.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.PeerId);
        string level = LogPolicy.NormalizeLevel(entry.Level);
        string debug = "DEBUG";
        bool hidePersonal = entry.ContainsPersonalData && !string.Equals(level, debug, StringComparison.Ordinal);
        NodeValue? data = entry.Data;
        string message = Message(entry.Message, patterns);
        if (hidePersonal)
        {
            message = "[personal data omitted]";
            data = null;
        }
        else
        {
            if (data is not null)
            {
                if (data.Kind == EValueKind.Binary)
                {
                    data = NodeValue.FromBytes(Binary(data.Binary, patterns));
                }
                else
                {
                    data = NodeValue.FromJson(JsonSerializer.SerializeToElement(Json(data.Json, patterns)));
                }
            }
        }
        return entry with
        {
            Level = level,
            PeerId = Text(entry.PeerId, patterns),
            Message = message,
            WorkflowId = Optional(entry.WorkflowId, patterns),
            NodeId = Optional(entry.NodeId, patterns),
            Data = data,
        };
    }

    /// <summary>構造を持つログ本文にも、付随データと同じ秘密項目の保護を適用します。</summary>
    /// <returns>JSON本文または通常のテキストを保護した文字列です。</returns>
    private static string Message(string value, IReadOnlyList<string> patterns)
    {
        ReadOnlySpan<char> trimmed = value.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && trimmed[0] is '{' or '[')
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                return Json(document.RootElement, patterns)!.ToJsonString();
            }
            catch (JsonException)
            {
                // JSONに似た通常の文章は、そのまま文字列として秘密値だけを伏せます。
            }
        }
        return Text(value, patterns);
    }

    /// <summary>文字列中の登録済み秘密を除去します。</summary>
    /// <returns>伏字にした文字列です。</returns>
    private static string Text(string value, IReadOnlyList<string> patterns)
    {
        foreach (string pattern in patterns)
        {
            value = value.Replace(pattern, "[redacted]", StringComparison.Ordinal);
        }
        return value;
    }

    /// <summary>省略値を維持して文字列を保護します。</summary>
    /// <returns>省略または伏字値です。</returns>
    private static string? Optional(string? value, IReadOnlyList<string> patterns)
    {
        if (value is null)
        {
            return null;
        }
        return Text(value, patterns);
    }

    /// <summary>JSONの構造を保ちつつ秘密項目と値を保護します。</summary>
    /// <returns>保護したJSON値です。</returns>
    private static JsonNode? Json(JsonElement value, IReadOnlyList<string> patterns)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            JsonObject result = new();
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string name = property.Name.ToUpperInvariant();
                bool secret = name is "PASSWORD" or "TOKEN" or "APIKEY" or "API_KEY" or "AUTHORIZATION" or "SECRET" or "ACCESS_TOKEN" or "REFRESH_TOKEN";
                if (secret)
                {
                    result[Text(property.Name, patterns)] = "[redacted]";
                }
                else
                {
                    string binaryName = "BINARY";
                    if (string.Equals(name, binaryName, StringComparison.Ordinal) && IsBinaryNodeValue(value))
                    {
                        result[Text(property.Name, patterns)] = ProtectedBase64(property.Value, patterns);
                    }
                    else
                    {
                        result[Text(property.Name, patterns)] = Json(property.Value, patterns);
                    }
                }
            }
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            JsonArray result = new();
            foreach (JsonElement item in value.EnumerateArray())
            {
                result.Add(Json(item, patterns));
            }
            return result;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return JsonValue.Create(Text(value.GetString() ?? string.Empty, patterns));
        }
        string raw = value.GetRawText();
        if (patterns.Contains(raw, StringComparer.Ordinal))
        {
            return JsonValue.Create("[redacted]");
        }
        return JsonNode.Parse(raw);
    }

    /// <summary>NodeValueのJSON表現に内包されたバイト列も変換前に保護します。</summary>
    /// <returns>Kindがバイナリである既知の値形式か。</returns>
    private static bool IsBinaryNodeValue(JsonElement value)
    {
        string kind = "kind";
        string binary = "binary";
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, kind, StringComparison.OrdinalIgnoreCase))
            {
                return (property.Value.ValueKind == JsonValueKind.String && string.Equals(property.Value.GetString(), binary, StringComparison.OrdinalIgnoreCase))
                    || (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int number) && number == (int)EValueKind.Binary);
            }
        }
        return false;
    }

    /// <summary>生バイト内の秘密を除いてからbase64へ戻します。</summary>
    /// <returns>保護済みbase64または不明内容の省略表示です。</returns>
    private static JsonValue ProtectedBase64(JsonElement value, IReadOnlyList<string> patterns)
    {
        if (value.ValueKind == JsonValueKind.String && value.TryGetBytesFromBase64(out byte[]? bytes))
        {
            return JsonValue.Create(Convert.ToBase64String(Binary(bytes, patterns)))!;
        }
        return JsonValue.Create("[unreadable binary omitted]")!;
    }

    /// <summary>UTF8とUTF16の秘密表現を共通の伏字へ置換します。</summary>
    /// <returns>呼出元を変更しない保護済みバイト列です。</returns>
    private static byte[] Binary(ReadOnlyMemory<byte> value, IReadOnlyList<string> patterns)
    {
        byte[] bytes = value.ToArray();
        foreach (string pattern in patterns)
        {
            bytes = Replace(bytes, Encoding.UTF8.GetBytes(pattern));
            bytes = Replace(bytes, Encoding.Unicode.GetBytes(pattern));
            bytes = Replace(bytes, Encoding.BigEndianUnicode.GetBytes(pattern));
        }
        return bytes;
    }

    /// <summary>生バイト中のUTF8またはUTF16秘密値を伏せます。</summary>
    /// <returns>保護したバイト列です。</returns>
    private static byte[] Replace(byte[] value, byte[] pattern)
    {
        int match = value.AsSpan().IndexOf(pattern);
        if (match < 0)
        {
            return value;
        }
        using MemoryStream result = new();
        int offset = 0;
        while (0 <= match)
        {
            result.Write(value.AsSpan(offset, match));
            result.Write("[redacted]"u8);
            offset += match + pattern.Length;
            match = value.AsSpan(offset).IndexOf(pattern);
        }
        result.Write(value.AsSpan(offset));
        return result.ToArray();
    }
}
