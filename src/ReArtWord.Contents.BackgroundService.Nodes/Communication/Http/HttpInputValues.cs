using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTP入力と明示型を対応させ、照会文字列の変換とJSON本文の検証を分けます。</summary>
internal static class HttpInputValues
{
    private static readonly HashSet<string> methods = new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.Ordinal);
    private static string MethodPort => "method";
    private static string AnyMethod => "*";
    private static string QueryTypePort => "queryType";
    private static string BodyTypePort => "bodyType";
    private static string BodyFormat => "bodyFormat";
    private static string TextFormat => "text";
    private static string JsonFormat => "json";
    private static string BinaryFormat => "binary";
    private static string AutomaticFormat => "auto";

    /// <summary>入力されたメソッドを優先し、既存の送信設定を維持します。</summary>
    /// <returns>大文字のメソッド、または待受の全メソッドを表す*です。</returns>
    internal static string Method(NodeExecutionContext context, bool server)
    {
        string fallback = "POST";
        if (server)
        {
            fallback = AnyMethod;
        }
        string method = new NodeSettings(context.Settings).ReadText(MethodPort, fallback) ?? fallback;
        if (context.Inputs.TryGetValue(MethodPort, out NodeValue? input))
        {
            if (input.Kind != EValueKind.Json || input.Json.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("http.method.type");
            }
            method = input.Json.GetString() ?? string.Empty;
        }
        if (!methods.Contains(method) && !(server && string.Equals(method, AnyMethod, StringComparison.Ordinal)))
        {
            throw new FormatException("http.method.invalid");
        }
        return method;
    }

    /// <summary>直接指定した型は、待受開始前にも検証します。</summary>
    /// <returns>入力型に関する問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        string? format = new NodeSettings(definition.Settings).ReadText(BodyFormat);
        bool text = string.Equals(format, TextFormat, StringComparison.Ordinal) || string.Equals(format, BinaryFormat, StringComparison.Ordinal);
        List<ValidationIssue> issues = [.. StructuredValueTypes.Validate(definition, "query")];
        if (!text)
        {
            issues.AddRange(StructuredValueTypes.Validate(definition, "body"));
        }
        if (string.Equals(format, BinaryFormat, StringComparison.Ordinal) || string.Equals(format, AutomaticFormat, StringComparison.Ordinal))
        {
            NodeSettings settings = new(definition.Settings);
            string graphQl = "graphql", modeKey = "mode";
            if (string.Equals(format, BinaryFormat, StringComparison.Ordinal) && string.Equals(settings.ReadText(modeKey), graphQl, StringComparison.Ordinal))
            {
                issues.Add(new("http.body.graphql", "GraphQL requires a JSON body, not Binary.", definition.Id, BodyFormat));
            }
            try
            {
                _ = HttpBodyFormats.Resolve(definition.Settings, null);
            }
            catch (FormatException)
            {
                issues.Add(new("http.body.mediaType", "Specify a concrete MIME type such as image/png or application/octet-stream.", definition.Id, "bodyMediaType"));
            }
        }
        foreach (string port in new[] { QueryTypePort, BodyTypePort })
        {
            if (text && string.Equals(port, BodyTypePort, StringComparison.Ordinal))
            {
                continue;
            }
            if (definition.Inputs?.GetValueOrDefault(port) is not {} input)
            {
                continue;
            }
            try
            {
                ValueTypeDefinition type = ValueTypeOperations.ReadType(input);
                if (string.Equals(port, QueryTypePort, StringComparison.Ordinal))
                {
                    RequireQueryObject(type);
                }
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            {
                issues.Add(new("http.parameter.type", "A valid parameter type definition is required.", definition.Id, port));
            }
        }
        return issues;
    }

    /// <summary>同名照会を配列へ対応させ、明示されたスカラーの重複は拒否します。</summary>
    /// <returns>型変換済み照会オブジェクトです。型未指定時は元の文字列を保ちます。</returns>
    internal static NodeValue Query(NodeExecutionContext context, IQueryCollection query)
    {
        ValueTypeDefinition? type = ReadType(context, QueryTypePort);
        if (type is not null)
        {
            RequireQueryObject(type);
        }
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, StringValues> entry in query)
        {
            ValueTypeDefinition? property = type?.Properties?.GetValueOrDefault(entry.Key);
            properties.Add(entry.Key, QueryValue(entry.Value, property).Json.Clone());
        }
        NodeValue result = NodeValue.FromObject(properties);
        if (type is not null)
        {
            ValueTypeOperations.ValidateValue(result, type);
            result = PathTypeMetadata.Apply(result, type);
        }
        return result;
    }

    /// <summary>本文の型を指定した場合だけJSONとして検証し、文字列から数値へ暗黙変換しません。</summary>
    /// <returns>型付きJSON本文、または従来互換の本文文字列です。</returns>
    internal static NodeValue Body(NodeExecutionContext context, string body, string? resolvedFormat = null)
    {
        string? format = resolvedFormat ?? new NodeSettings(context.Settings).ReadText(BodyFormat);
        if (string.Equals(format, TextFormat, StringComparison.Ordinal))
        {
            return NodeValue.FromObject(body);
        }
        ValueTypeDefinition? type = ReadType(context, BodyTypePort);
        if (string.Equals(format, JsonFormat, StringComparison.Ordinal))
        {
            using JsonDocument parsed = JsonDocument.Parse(body);
            NodeValue value = NodeValue.FromJson(parsed.RootElement);
            if (type is not null)
            {
                ValueTypeOperations.ValidateValue(value, type);
                value = PathTypeMetadata.Apply(value, type);
            }
            return value;
        }
        if (type is null)
        {
            string mode = "bodyMode";
            string automatic = "auto";
            if (string.Equals(new NodeSettings(context.Settings).ReadText(mode), automatic, StringComparison.Ordinal))
            {
                return ValueTypeOperations.ParseAuto(body);
            }
            return NodeValue.FromObject(body);
        }
        NodeValue result;
        if (string.IsNullOrEmpty(body) && type.Optional)
        {
            result = NodeValue.FromObject<object?>(null);
        }
        else
        {
            using JsonDocument document = JsonDocument.Parse(body);
            result = NodeValue.FromJson(document.RootElement);
        }
        ValueTypeOperations.ValidateValue(result, type);
        return PathTypeMetadata.Apply(result, type);
    }

    /// <summary>繰返し値の型変換規則を一箇所へ集約します。</summary>
    /// <returns>単一値または配列です。</returns>
    private static NodeValue QueryValue(StringValues values, ValueTypeDefinition? type)
    {
        string array = "array";
        string any = "any";
        if (type is not null && string.Equals(type.Type, array, StringComparison.Ordinal))
        {
            JsonElement[] items = values.Select(value => QueryText(value ?? string.Empty, type.ElementType).Json.Clone()).ToArray();
            NodeValue result = NodeValue.FromObject(items);
            ValueTypeOperations.ValidateValue(result, type);
            return PathTypeMetadata.Apply(result, type);
        }
        if (values.Count != 1)
        {
            if (type is not null && !string.Equals(type.Type, any, StringComparison.Ordinal))
            {
                throw new FormatException("http.query.repeatedScalar");
            }
            return NodeValue.FromObject(values.Select(value => value ?? string.Empty).ToArray());
        }
        return QueryText(values[0] ?? string.Empty, type);
    }

    /// <summary>未指定とanyは文字列を維持し、指定型だけを変換します。</summary>
    /// <returns>共通の型規則で処理した一値です。</returns>
    private static NodeValue QueryText(string value, ValueTypeDefinition? type)
    {
        string any = "any";
        if (type is null || string.Equals(type.Type, any, StringComparison.Ordinal))
        {
            return NodeValue.FromObject(value);
        }
        return ValueTypeOperations.ParseText(value, type);
    }

    /// <summary>待受開始前に、今回の型指定を確定します。</summary>
    internal static void RequireTypes(NodeExecutionContext context)
    {
        ValueTypeDefinition? query = ReadType(context, QueryTypePort);
        if (query is not null)
        {
            RequireQueryObject(query);
        }
        string? format = new NodeSettings(context.Settings).ReadText(BodyFormat);
        if (string.Equals(format, BinaryFormat, StringComparison.Ordinal) || string.Equals(format, AutomaticFormat, StringComparison.Ordinal))
        {
            _ = HttpBodyFormats.Resolve(context.Settings, null);
        }
        if (!string.Equals(format, TextFormat, StringComparison.Ordinal) && !string.Equals(format, BinaryFormat, StringComparison.Ordinal))
        {
            _ = ReadType(context, BodyTypePort);
        }
    }

    /// <summary>型指定ノードと直書き型定義を同じ入口で読みます。</summary>
    /// <returns>検証済み型定義または未指定です。</returns>
    private static ValueTypeDefinition? ReadType(NodeExecutionContext context, string port)
    {
        return StructuredValueTypes.Read(context.Settings, context.Inputs, port[..^4]);
    }

    /// <summary>照会全体はキーを持つオブジェクトとして扱います。</summary>
    private static void RequireQueryObject(ValueTypeDefinition type)
    {
        string objectType = "object";
        string any = "any";
        if (!string.Equals(type.Type, objectType, StringComparison.Ordinal) && !string.Equals(type.Type, any, StringComparison.Ordinal))
        {
            throw new FormatException("http.query.objectTypeRequired");
        }
    }
}
