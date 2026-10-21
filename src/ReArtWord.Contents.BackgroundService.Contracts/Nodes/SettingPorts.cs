using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>設定欄に接続するポート名を、通常の値入力と区別します。</summary>
public static class SettingPorts
{
    private static string Prefix => "$settings.";

    /// <summary>保存可能な設定接続ポート名を作成します。</summary>
    /// <returns>通常ポートと衝突しない予約名です。</returns>
    public static string Name(string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        return Prefix + property;
    }

    /// <summary>設定欄の予約名前空間を通常入力と区別します。</summary>
    /// <returns>項目名が未指定の予約名もtrueです。</returns>
    public static bool IsSetting(string port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return port.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>通常入力を設定入力と誤認しません。</summary>
    /// <returns>設定項目名、または通常入力を示すnullです。</returns>
    public static string? Property(string port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (IsSetting(port) && Prefix.Length < port.Length)
        {
            return port[Prefix.Length..];
        }
        return null;
    }

    /// <summary>実際にスキーマへ宣言された設定欄だけを接続可能にします。</summary>
    /// <returns>JSON設定へ変換できる宣言があるかです。</returns>
    public static bool IsDeclared(NodeDescriptor descriptor, string port)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string key = "properties";
        string? property = Property(port);
        return property is not null && descriptor.SettingsSchema.ValueKind == JsonValueKind.Object
            && descriptor.SettingsSchema.TryGetProperty(key, out JsonElement properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(property, out JsonElement schema) && CanConnect(schema);
    }

    /// <summary>直接編集専用欄にはグラフにも実行定義にも設定入力を公開しません。</summary>
    /// <returns>未指定は従来どおり接続可能です。</returns>
    public static bool CanConnect(JsonElement schema)
    {
        string key = "x-connectable";
        return schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.False;
    }

    /// <summary>有限処理の設定は実行時入力を既定とし、構成時に必要な項目だけを除外します。</summary>
    /// <returns>待受の開始条件や明示した静的契約以外は、純粋な値ノードにも実行直前に供給できます。</returns>
    public static bool AllowsRuntimeValue(NodeDescriptor descriptor, string port)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string propertiesKey = "properties";
        string runtimeKey = "x-runtime-input";
        string? property = Property(port);
        return descriptor.Kind == ENodeKind.Action && !descriptor.Outputs.Any(output => output.ValueKind == EPortValueKind.PcSelection) && property is not null
            && IsDeclared(descriptor, port)
            && (!descriptor.SettingsSchema.GetProperty(propertiesKey).GetProperty(property).TryGetProperty(runtimeKey, out JsonElement runtime)
                || runtime.ValueKind != JsonValueKind.False);
    }
}
