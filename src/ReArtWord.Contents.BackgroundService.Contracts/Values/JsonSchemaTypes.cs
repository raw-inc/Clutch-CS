using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>JSONスキーマの表示用の型と元の制約を分離します。ここではデータを検証しません。</summary>
public static class JsonSchemaTypes
{
    private static readonly string[] objectCandidates = ["object", "any"];
    private static string ObjectType => "object";

    /// <summary>元スキーマを保持し、確実に分かる型・項目だけをポートへ伝えます。</summary>
    /// <returns>実行時検証用スキーマを含む型です。</returns>
    public static ValueTypeDefinition Read(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.String)
        {
            string text = schema.GetString()!;
            if (65536 < text.Length)
            {
                throw new FormatException("schema.definition");
            }
            schema = JsonSerializer.Deserialize<JsonElement>(text);
        }
        if (schema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False) || 65536 < schema.GetRawText().Length)
        {
            throw new FormatException("schema.definition");
        }
        int remaining = 1024;
        return DisplayType(Read(schema, schema, 0, ref remaining)) with { Schema = schema.Clone() };
    }

    /// <summary>条件処理の中間候補を、型を保証できるObjectの配下だけに残します。</summary>
    /// <returns>通常の型契約として利用できる表示型です。</returns>
    private static ValueTypeDefinition DisplayType(ValueTypeDefinition type)
        => type with
        {
            Properties = string.Equals(type.Type, ObjectType, StringComparison.Ordinal) ? type.Properties?.ToDictionary(pair => pair.Key, pair => DisplayType(pair.Value), StringComparer.Ordinal)
                                                                                        : null,
            ElementType = type.ElementType is null ? null
                                                   : DisplayType(type.ElementType)
        };

    /// <summary>複合条件などから断定できない型はanyとして維持します。</summary>
    /// <returns>任意のプロパティ名を保持した表示用の型です。</returns>
    private static ValueTypeDefinition Read(JsonElement schema, JsonElement root, int depth, ref int remaining)
    {
        ValueTypeDefinition basis = ReadShape(schema, root, depth, ref remaining);
        if (schema.ValueKind != JsonValueKind.Object || 16 <= depth || remaining < 0)
        {
            return basis;
        }
        string allOf = "allOf";
        if (schema.TryGetProperty(allOf, out JsonElement branches) && branches.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement branch in branches.EnumerateArray())
            {
                if (remaining < 0)
                {
                    break;
                }
                basis = Candidates(basis, Read(branch, root, depth + 1, ref remaining), depth);
            }
        }
        string condition = "if";
        if (schema.TryGetProperty(condition, out _))
        {
            foreach (string branchName in new[] { "then", "else" })
            {
                if (schema.TryGetProperty(branchName, out JsonElement branch) && 0 <= remaining)
                {
                    basis = Candidates(basis, Read(branch, root, depth + 1, ref remaining), depth);
                }
            }
        }
        return basis;
    }

    /// <summary>条件付き項目は候補名だけを追加し、無条件の必須・具体型へ格上げしません。</summary>
    /// <returns>元の型保証を維持した項目候補です。</returns>
    private static ValueTypeDefinition Candidates(ValueTypeDefinition basis, ValueTypeDefinition candidate, int depth)
    {
        if (candidate.Properties is null || !objectCandidates.Contains(basis.Type, StringComparer.Ordinal) || 16 <= depth)
        {
            return basis;
        }
        Dictionary<string, ValueTypeDefinition> properties = new(basis.Properties ?? new Dictionary<string, ValueTypeDefinition>(), StringComparer.Ordinal);
        foreach ((string name, ValueTypeDefinition child) in candidate.Properties)
        {
            if (properties.TryGetValue(name, out ValueTypeDefinition? existing))
            {
                properties[name] = Candidates(existing, child, depth + 1);
            }
            else
            {
                if (properties.Count < 128)
                {
                    properties[name] = Candidates(new("any", Optional: true), child, depth + 1);
                }
            }
        }
        return basis with { Properties = properties };
    }

    /// <summary>基本形とローカル参照を、走査上限を共有して読み取ります。</summary>
    /// <returns>無条件の型情報です。</returns>
    private static ValueTypeDefinition ReadShape(JsonElement schema, JsonElement root, int depth, ref int remaining)
    {
        if (schema.ValueKind != JsonValueKind.Object || 16 <= depth || --remaining < 0)
        {
            return new("any");
        }
        string reference = "$ref";
        if (schema.TryGetProperty(reference, out JsonElement link) && link.ValueKind == JsonValueKind.String && link.GetString() is { } path && path.StartsWith('#'))
        {
            string pointer = Uri.UnescapeDataString(path[1..]);
            if (JsonPointer.IsValid(pointer) && JsonPointer.TrySelect(NodeValue.FromJson(root), pointer, out NodeValue? target))
            {
                return Read(target!.Json, root, depth + 1, ref remaining);
            }
        }
        string typeKey = "type";
        string? name = null;
        bool nullable = false;
        if (schema.TryGetProperty(typeKey, out JsonElement type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                name = type.GetString();
            }
            if (type.ValueKind == JsonValueKind.Array && type.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            {
                string nullType = "null";
                string[] names = type.EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                nullable = names.Contains(nullType, StringComparer.Ordinal);
                string[] concrete = names.Where(item => !string.Equals(item, nullType, StringComparison.Ordinal)).ToArray();
                name = concrete.Length == 1 ? concrete[0]
                                            : null;
            }
        }
        string arrayType = "array";
        string objectType = "object";
        string itemsKey = "items";
        string propertiesKey = "properties";
        string requiredKey = "required";
        if (string.Equals(name, arrayType, StringComparison.Ordinal))
        {
            ValueTypeDefinition? element = null;
            if (schema.TryGetProperty(itemsKey, out JsonElement items))
            {
                element = Read(items, root, depth + 1, ref remaining);
            }
            return new(arrayType, element, Optional: nullable);
        }
        if ((name is null || string.Equals(name, objectType, StringComparison.Ordinal)) && schema.TryGetProperty(propertiesKey, out JsonElement definitions) && definitions.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> required = new(StringComparer.Ordinal);
            if (schema.TryGetProperty(requiredKey, out JsonElement requiredValues) && requiredValues.ValueKind == JsonValueKind.Array)
            {
                required.UnionWith(requiredValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
            }
            Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in definitions.EnumerateObject())
            {
                if (128 <= properties.Count)
                {
                    return new(objectType, Optional: nullable);
                }
                ValueTypeDefinition child = Read(property.Value, root, depth + 1, ref remaining);
                if (!properties.TryAdd(property.Name, child with { Optional = child.Optional || !required.Contains(property.Name) }))
                {
                    throw new FormatException("schema.properties.duplicate");
                }
            }
            return new(name ?? "any", Properties: properties, Optional: nullable);
        }
        string scalar = name switch { "string" or "boolean" or "integer" or "number" or "object" => name, _ => "any" };
        string formatKey = "format";
        bool pathFormat = scalar is "string" && schema.TryGetProperty(formatKey, out JsonElement format) && format.ValueKind == JsonValueKind.String && format.GetString() is "path";
        if (pathFormat)
        {
            scalar = "path";
        }
        return new(scalar, Optional: nullable);

    }
}
