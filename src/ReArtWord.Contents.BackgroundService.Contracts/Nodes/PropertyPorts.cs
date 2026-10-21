using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>オブジェクトの個別出力を既存出力からの参照として扱い、値を常時複製しません。</summary>
public static class PropertyPorts
{
    private static readonly string[] unknownObjects = ["any", "object"];

    /// <summary>親出力と任意のキー名を衝突しない識別子へ変換します。</summary>
    /// <returns>JSON Pointerと同じエスケープを使う識別子です。</returns>
    public static string Name(string output, string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return output + "/" + property.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>正規の一階層プロパティ出力だけを解釈します。</summary>
    /// <returns>親出力とプロパティ名、または未該当です。</returns>
    public static (string Output, string Property)? Parse(string port)
    {
        ArgumentNullException.ThrowIfNull(port);
        int separator = port.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || port.IndexOf('/', separator + 1) != -1 || 4096 < port.Length || !JsonPointer.IsValid(port[separator..]))
        {
            return null;
        }
        string property = port[(separator + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return (port[..separator], property);
    }

    /// <summary>既存の出力値と同じJSON Pointer規則でプロパティを取り出します。</summary>
    /// <returns>直接出力またはプロパティ出力が取得できたかです。</returns>
    public static bool TryRead(IReadOnlyDictionary<string, NodeValue> outputs, string port, out NodeValue? value, bool requireExisting = false)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.TryGetValue(port, out value))
        {
            return true;
        }
        if (Parse(port) is { } field && outputs.TryGetValue(field.Output, out NodeValue? parent) && parent.Kind == EValueKind.Json && parent.Json.ValueKind == JsonValueKind.Object)
        {
            string path = Name(string.Empty, field.Property);
            if (requireExisting)
            {
                return JsonPointer.TrySelect(parent, path, out value);
            }
            value = JsonPointer.Select(parent, path);
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>型が分かる全オブジェクト出力に個別のプロパティ口を追加します。</summary>
    /// <returns>元の出力を維持した描画・接続検証用契約です。</returns>
    public static NodeDescriptor Resolve(NodeDescriptor descriptor, NodeDefinition node, WorkflowDefinition? document, WorkflowTypeInference? inference = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(node);
        if (descriptor.IsTerminal)
        {
            return descriptor;
        }
        document ??= new("property-inspection", "property-inspection", [node], []);
        inference ??= new(document, [descriptor]);
        List<PortDescriptor> outputs = descriptor.Outputs.ToList();
        foreach (PortDescriptor output in descriptor.Outputs.Where(port => Parse(port.Name) is null))
        {
            ValueTypeDefinition? type = inference.PropertyType(node.Id, output.Name);
            string any = "any";
            bool candidatesOnly = type?.Properties is { Count: > 0 } hints && hints.Values.All(property => property.Optional && string.Equals(property.Type, any, StringComparison.Ordinal));
            if (type?.Properties is { } properties)
            {
                outputs.AddRange(properties.Take(128).Select(property => new PortDescriptor(Name(output.Name, property.Key), EPortValueKind.Json)).Where(port => Parse(port.Name) is not null));
            }
            if ((type?.Properties is null || candidatesOnly) && (type is null || unknownObjects.Contains(type.Type, StringComparer.Ordinal)))
            {
                int remaining = 128 - outputs.Count(port => Parse(port.Name) is { } field && string.Equals(field.Output, output.Name, StringComparison.Ordinal));
                outputs.AddRange(document.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)
                    && Parse(wire.FromPort) is { } field && string.Equals(field.Output, output.Name, StringComparison.Ordinal))
                    .Select(wire => new PortDescriptor(wire.FromPort, EPortValueKind.Json)).DistinctBy(port => port.Name)
                    .Where(port => !outputs.Any(existing => string.Equals(existing.Name, port.Name, StringComparison.Ordinal))).Take(Math.Max(0, remaining)));
            }
        }
        return descriptor with { Outputs = outputs.DistinctBy(port => port.Name).ToArray() };
    }
}
