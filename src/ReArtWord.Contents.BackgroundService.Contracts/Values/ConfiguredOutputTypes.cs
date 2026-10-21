using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>変換・抽出の設定から出力の種類を推測します。処理の実行や外部データの取得は行いません。</summary>
internal sealed class ConfiguredOutputTypes(WorkflowLiteralValues literals, Func<string, string, bool, ValueTypeDefinition?> portType, WorkflowSettingValues settings)
{
    private static string ValuePort => "value";
    private static string FieldsSetting => "$settings.fields";
    private static string ReplySetting => "$settings.awaitReply";
    private static string PathSetting => "path";

    /// <summary>設定が接続で供給される場合も、読み取れた値だけを使います。</summary>
    /// <returns>成功時の出力型、または未確定です。</returns>
    internal ValueTypeDefinition? Get(NodeDefinition node) => node.Type switch
    {
        "control.valueMerge" => ValueMergeTypes.ResultType(Enumerable.Range(1, ConcatPorts.Maximum).Select(ConcatPorts.Name).Where(port => literals.HasInput(node, port)).Select(port => portType(node.Id, port, false))),
        "control.toggleButton" => ValueTypeInference.CommonChoice(portType(node.Id, "offValue", false), portType(node.Id, "onValue", false)),
        "data.concat" => ConcatValues.Infer(node, literals, portType),
        "data.keyValue.get" => KeyValueLookupTypes.Infer(node, literals, portType),
        "data.defaultIfNull" => DefaultIfNullValues.Infer(node, literals, portType),
        "data.keyValue.key" or "data.keyValue.value" => KeyValueParts.Infer(portType(node.Id, ValuePort, false), node.Type is "data.keyValue.key"),
        "data.pick" => ArrayPickTypes.Infer(node, literals, portType),
        "data.randomPick" => ArrayPickTypes.Element(portType(node.Id, ValuePort, false)),
        "math.random" => Text(node, "mode") switch { "integer" => new("integer"), "decimal" => new("number"), null when !literals.HasInput(node, "$settings.mode") => new("integer"), _ => new("number") },
        "text.regex" => RegexTextPattern.Infer(node, settings),
        "data.object.expand" => ExpandObject(node),
        "data.json.parse" or "data.yaml.parse" or "data.toml.parse" => Parsed(node),
        "data.binary.parse" => Binary(node),
        "data.extract" => Extract(node),
        "data.transform" => Transform(node),
        "tcp.request" => Tcp(node),
        "database.sqlite" or "database.postgresql" => Relational(node),
        "database.redis" => Redis(node),
        "scheduling.timer" => literals.HasInput(node, ValuePort) ? portType(node.Id, ValuePort, false)
                                                                 : new("string"),
        _ => null,
    };

    /// <summary>明示した展開型を優先し、未指定なら接続元の構造を一階層ずつ引き継ぎます。</summary>
    /// <returns>表示するObjectの構造です。</returns>
    private ValueTypeDefinition? ExpandObject(NodeDefinition node)
    {
        string prefix = "object";
        ValueTypeDefinition? declared = StructuredTypeSettings.Read(node, settings, prefix);
        if (declared is not null || !StructuredTypeSettings.Automatic(node, settings, prefix))
        {
            return declared;
        }
        return portType(node.Id, ValuePort, false) ?? new("object");
    }

    /// <summary>HTTPパラメーターの指定型と未指定時の互換動作を区別します。</summary>
    /// <returns>手動・スキーマの型、または自動受信の型です。</returns>
    internal ValueTypeDefinition? HttpParameter(NodeDefinition node, string port)
    {
        string query = "query";
        string body = "body";
        string bodyFormat = "bodyFormat";
        string textFormat = "text";
        string jsonFormat = "json";
        string binaryFormat = "binary";
        string? format = settings.Setting(node, bodyFormat)?.GetString();
        bool isBody = string.Equals(port, body, StringComparison.Ordinal);
        if (isBody && string.Equals(format, binaryFormat, StringComparison.Ordinal))
        {
            return new("binary");
        }
        if (isBody && string.Equals(format, textFormat, StringComparison.Ordinal))
        {
            return new("string");
        }
        if (isBody && format is null && settings.HasInput(node, SettingPorts.Name(bodyFormat)))
        {
            return new("any");
        }
        string automaticFormat = "auto";
        if (isBody && string.Equals(format, automaticFormat, StringComparison.Ordinal))
        {
            return new("any");
        }
        ValueTypeDefinition? declared = StructuredTypeSettings.Read(node, settings, port);
        if (declared is not null || !StructuredTypeSettings.Automatic(node, settings, port))
        {
            return declared;
        }
        string mode = "bodyMode";
        string automatic = "auto";
        if (isBody && (string.Equals(format, jsonFormat, StringComparison.Ordinal) || string.Equals(settings.Setting(node, mode)?.GetString(), automatic, StringComparison.Ordinal)))
        {
            return new("any");
        }
        return string.Equals(port, query, StringComparison.Ordinal) ? new("object")
                                                                    : new("string");
    }

    /// <summary>解析結果全体のスキーマを表示へ使い、未指定なら従来の推測を維持します。</summary>
    /// <returns>JSON/YAML/TOMLの解析後のデータ型です。</returns>
    private ValueTypeDefinition? Parsed(NodeDefinition node)
    {
        ValueTypeDefinition? declared = StructuredTypeSettings.Read(node, settings, ValuePort);
        if (declared is not null || !StructuredTypeSettings.Automatic(node, settings, ValuePort))
        {
            return declared;
        }
        return node.Type switch { "data.json.parse" => Json(node), "data.toml.parse" => new("object"), _ => new("any") };
    }

    /// <summary>SQLモードが固定の場合だけ、行配列と変更結果を区別します。</summary>
    /// <returns>SQL列自体の型は実行せず推定しません。</returns>
    private ValueTypeDefinition? Relational(NodeDefinition node)
    {
        string? mode = Text(node, "mode");
        string setting = "$settings.mode";
        if (mode is null && !literals.HasInput(node, setting))
        {
            mode = "query";
        }
        return mode switch { "query" => new("array", new("object")), "execute" => new("object"), _ => null };
    }

    /// <summary>取得形式が固定の場合だけRedisの値型を公開します。</summary>
    /// <returns>JSONは内容が未確定で、キー欠落時には値自体がありません。</returns>
    private ValueTypeDefinition? Redis(NodeDefinition node)
    {
        string? format = Text(node, "format");
        string setting = "$settings.format";
        if (format is null && !literals.HasInput(node, setting))
        {
            format = "text";
        }
        return format switch { "text" => new("string"), "binary" => new("binary"), _ => null };
    }

    /// <summary>JSON解析の固定入力だけを読み、YAML等の別形式の解析器は複製しません。</summary>
    /// <returns>入力テキストから分かった値の種類です。</returns>
    private ValueTypeDefinition? Json(NodeDefinition node)
    {
        NodeValue? input = literals.Input(node, "value");
        if (input?.Kind != EValueKind.Json || input.Json.ValueKind != JsonValueKind.String || input.Json.GetString() is not { } text)
        {
            return null;
        }
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
            return ValueTypeInference.FromJson(parsed.RootElement, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>フィールド指定のないバイト列と、指定した項目を持つ解析結果を区別します。</summary>
    /// <returns>設定の有無を確認できた場合の型です。</returns>
    private ValueTypeDefinition? Binary(NodeDefinition node)
    {
        NodeValue? fields = literals.Setting(node, "fields");
        if (fields is null)
        {
            return literals.HasInput(node, FieldsSetting) ? null
                                                          : new("binary");
        }
        if (fields.Json.ValueKind == JsonValueKind.Array)
        {
            return new("object");
        }
        return null;
    }

    /// <summary>送信だけの場合と、返信を値として受け取る場合を区別します。</summary>
    /// <returns>送信確認または受信バイト列の型です。</returns>
    private ValueTypeDefinition? Tcp(NodeDefinition node)
    {
        NodeValue? reply = literals.Setting(node, "awaitReply");
        if (reply is null && literals.HasInput(node, ReplySetting))
        {
            return null;
        }
        return reply?.Json.ValueKind switch
        {
            JsonValueKind.True => new("binary"),
            JsonValueKind.False or null => new("object"),
            _ => null,
        };
    }

    /// <summary>抽出先が存在しない場合のnullも型に含めます。</summary>
    /// <returns>抽出方法に応じた型です。</returns>
    private ValueTypeDefinition? Extract(NodeDefinition node)
    {
        ValueTypeDefinition? input = portType(node.Id, "value", false);
        return Text(node, "mode") switch
        {
            "pointer" => Text(node, PathSetting) is { } path ? ValueTypeInference.Select(input, path)
                                                             : null,
            "row" => NullableElement(input),
            "cell" => NullableElement(NullableElement(input)),
            "line" => new("string", Optional: true),
            "regex" => new("array", new("string"), Optional: true),
            "binary" => Binary(node),
            _ => null,
        };
    }

    /// <summary>配列の長さが分からないため、要素がない可能性を残します。</summary>
    /// <returns>省略可能な要素型です。</returns>
    private static ValueTypeDefinition? NullableElement(ValueTypeDefinition? type)
    {
        string array = "array";
        if (type?.ElementType is { } element && string.Equals(type.Type, array, StringComparison.Ordinal))
        {
            return element with { Optional = true };
        }
        return null;
    }

    /// <summary>値の対応表と、項目にパス参照を含むオブジェクト生成を区別します。</summary>
    /// <returns>変換後の値の種類です。</returns>
    private ValueTypeDefinition? Transform(NodeDefinition node)
    {
        string? operation = Text(node, "operation");
        switch (operation)
        {
            case "concat": return new("string");
            case "set" or "remove" or "select" or "rename": return new("object");
            case "map":
                return MappedValue(node);
            case "create":
                NodeValue? fields = literals.Setting(node, "fields");
                if (fields?.Json.ValueKind == JsonValueKind.Object)
                {
                    int remaining = 1024;
                    Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
                    foreach (JsonProperty field in fields.Json.EnumerateObject())
                    {
                        ValueTypeDefinition type = Mapping(field.Value, portType(node.Id, "value", false), 0, ref remaining) ?? new("any");
                        if (128 <= properties.Count || remaining < 0 || !properties.TryAdd(field.Name, type))
                        {
                            return new("object");
                        }
                    }
                    return new("object", Properties: properties);
                }
                return new("object");
            default: return null;
        }
    }

    /// <summary>対応先の型が揃う場合だけ補足し、対応しない値のnullを保持します。</summary>
    /// <returns>固定入力の対応先、または全候補に共通する型です。</returns>
    private ValueTypeDefinition? MappedValue(NodeDefinition node)
    {
        NodeValue? mapping = literals.Setting(node, "mapping");
        if (mapping?.Json.ValueKind != JsonValueKind.Array || 1024 < mapping.Json.GetArrayLength())
        {
            return null;
        }
        NodeValue? input = literals.Input(node, "value");
        ValueTypeDefinition? common = null;
        bool first = true;
        string fromKey = "from";
        string toKey = "to";
        foreach (JsonElement entry in mapping.Json.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(fromKey, out JsonElement from) || !entry.TryGetProperty(toKey, out JsonElement to))
            {
                return null;
            }
            ValueTypeDefinition? type = ValueTypeInference.FromJson(to, 0);
            if (input?.Kind == EValueKind.Json && JsonElement.DeepEquals(input.Json, from))
            {
                return type;
            }
            if (input is null && !first && !ValueTypeInference.Same(common, type))
            {
                return null;
            }
            common = type;
            first = false;
        }
        return input is null && common is not null ? common with { Optional = true }
                                                   : null;
    }

    /// <summary>構造の走査量を制限し、配列や項目の型を再帰的に調べます。</summary>
    /// <returns>確認できた構造です。</returns>
    private static ValueTypeDefinition? Mapping(JsonElement value, ValueTypeDefinition? input, int depth, ref int remaining)
    {
        if (32 <= depth || --remaining < 0)
        {
            return null;
        }
        string pathKey = "$path";
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().Count() == 1 && value.TryGetProperty(pathKey, out JsonElement path))
            {
                return path.ValueKind == JsonValueKind.String ? ValueTypeInference.Select(input, path.GetString())
                                                              : null;
            }
            Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                ValueTypeDefinition type = Mapping(property.Value, input, depth + 1, ref remaining) ?? new("any");
                if (128 <= properties.Count || remaining < 0 || !properties.TryAdd(property.Name, type))
                {
                    return new("object");
                }
            }
            return new("object", Properties: properties);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            ValueTypeDefinition? element = null;
            bool first = true;
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValueTypeDefinition? next = Mapping(item, input, depth + 1, ref remaining);
                if (remaining < 0 || !first && !ValueTypeInference.Same(element, next))
                {
                    return new("array");
                }
                element = next;
                first = false;
            }
            return new("array", element);
        }
        return ValueTypeInference.FromJson(value, depth);
    }

    /// <summary>文字列として確認できた設定だけを使います。</summary>
    /// <returns>設定値または未確定です。</returns>
    private string? Text(NodeDefinition node, string name) => literals.Setting(node, name)?.GetString();
}
