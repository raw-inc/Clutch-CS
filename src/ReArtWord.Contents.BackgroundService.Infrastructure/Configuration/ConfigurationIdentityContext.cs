using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;

/// <summary>元操作の生成方式を固定条件と一緒に保存し、旧記録を新しいIDへ読み替えません。</summary>
internal static class ConfigurationIdentityContext
{
    private static string Marker => "$clutchDerivedIds";
    private static string Version => "uuid8-v1";

    /// <summary>新方式の固定条件は、旧コードが復元条件として使えない形式で保持します。</summary>
    /// <returns>旧形式のままの値、または生成方式を明示した固定条件です。</returns>
    internal static JsonElement? Write(JsonElement? value, EDerivedIdFormat format)
    {
        if (format == EDerivedIdFormat.Legacy)
        {
            return value;
        }
        if (format != EDerivedIdFormat.Uuid8)
        {
            throw new InvalidDataException("設定操作のID生成方式に対応していません。");
        }

        // 旧コードはContextを参加集合等として直接読みます。新方式の記録をその形のまま残すと、
        // 旧アルゴリズムで別の段階IDを作って再開できてしまうため、明示した外側の形式で区別します。
        return JsonSerializer.SerializeToElement(new Envelope(Version, value), ContractJson.Options);
    }

    /// <summary>方式のない記録を旧方式として読み、未対応形式を推測で変換しません。</summary>
    /// <returns>元の固定条件と、そこで使われた生成方式です。</returns>
    internal static ConfigurationOperation Read(ConfigurationOperation operation)
    {
        if (operation.Context is not { ValueKind: JsonValueKind.Object } context || !context.TryGetProperty(Marker, out _))
        {
            return operation;
        }
        Envelope? envelope = context.Deserialize<Envelope>(ContractJson.Options);
        if (envelope is null || !string.Equals(envelope.Version, Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存された設定操作のID生成方式に対応していません。");
        }
        return operation with { Context = envelope.Value, IdentityFormat = EDerivedIdFormat.Uuid8 };
    }

    /// <summary>旧形式には存在しない識別子で、保存世代を明示します。</summary>
    private sealed record Envelope([property: JsonPropertyName("$clutchDerivedIds")] string Version, JsonElement? Value);
}
