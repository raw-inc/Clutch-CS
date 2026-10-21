using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields.Types;

/// <summary>任意の項目名と入れ子型をJSONの手書きなしで編集します。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせます。")]
public sealed partial class TypeDefinitionEditor
{
    /// <summary>公開境界の最上位でバイナリと実行PC選択を許可します。</summary>
    [Parameter]
    public bool IncludeRuntimeTypes
    {
        get; set;
    }

    /// <summary>辞書のキー欄では、言語間で一意に表せる非nullスカラーだけを選択します。</summary>
    [Parameter]
    public bool DictionaryKeyOnly
    {
        get; set;
    }

    private static readonly string[] kinds = ["any", "string", "path", "boolean", "byte", "integer", "int64", "number", "decimal", "char", "array", "object", "dictionary"];
    private string? problem;
    private IEnumerable<string> Kinds => DictionaryKeyOnly ? ["string", "integer", "int64", "boolean", "byte", "char"]
                                                           : IncludeRuntimeTypes && Depth == 0 ? kinds.Concat(["binary", "pc-selection"])
                                                                                               : kinds;

    /// <summary>現在の型定義です。</summary>
    [Parameter]
    public JsonElement Value
    {
        get; set;
    }

    /// <summary>接続済み設定などを読み取り専用にします。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>深い型で無制限に編集部品を生成しません。</summary>
    [Parameter]
    public int Depth
    {
        get; set;
    }

    /// <summary>型定義の変更を親へ通知します。</summary>
    [Parameter]
    public EventCallback<JsonElement> ValueChanged
    {
        get; set;
    }

    /// <summary>重複した項目名を確定済みとして保存しません。</summary>
    [Parameter]
    public EventCallback<string?> InvalidChanged
    {
        get; set;
    }

    /// <summary>保存値を直接変更せず編集用の複製を作ります。</summary>
    /// <returns>未指定は任意型です。</returns>
    private JsonObject Read()
    {
        JsonObject value = Value.ValueKind == JsonValueKind.Object ? JsonNode.Parse(Value.GetRawText())!.AsObject()
                                                                   : new();
        string typeKey = "type";
        string optionalKey = "optional";
        if (value[typeKey] is not JsonValue kind || !kind.TryGetValue(out string? name) || !Kinds.Contains(name, StringComparer.Ordinal))
        {
            value[typeKey] = "any";
        }
        if (value[optionalKey] is not JsonValue optional || !optional.TryGetValue<bool>(out _))
        {
            value.Remove(optionalKey);
        }
        value.Remove("schema");
        return value;
    }

    /// <summary>子の型を表示可能なJSONへ変換します。</summary>
    /// <returns>独立した型定義です。</returns>
    private static JsonElement Element(JsonNode? value) => JsonSerializer.SerializeToElement(value ?? new JsonObject { ["type"] = "any" });

    /// <summary>項目の順序を維持します。</summary>
    /// <returns>現在の項目一覧です。</returns>
    private static JsonObject Properties(JsonObject value) => value["properties"] as JsonObject ?? new();

    /// <summary>型に不要な以前の構造を持ち越しません。</summary>
    /// <returns>選択した型の保存です。</returns>
    private Task ChangeTypeAsync(string name, CancellationToken cancellationToken = default)
    {
        JsonObject value = new() { ["type"] = name };
        switch (name)
        {
            case "array": value["elementType"] = new JsonObject { ["type"] = "any" }; break;
            case "object": value["properties"] = new JsonObject(); break;
            case "dictionary":
                value["type"] = "object";
                value["keyType"] = new JsonObject { ["type"] = "string" };
                value["valueType"] = new JsonObject { ["type"] = "any" };
                break;
        }
        return CommitAsync(value, cancellationToken);
    }

    /// <summary>nullと項目の省略を許可するか変更します。</summary>
    /// <returns>型定義の保存です。</returns>
    private Task OptionalAsync(bool optional, CancellationToken cancellationToken = default)
    {
        JsonObject value = Read();
        value["optional"] = optional;
        return CommitAsync(value, cancellationToken);
    }

    /// <summary>配列の要素型を変更します。</summary>
    /// <returns>親の型定義の保存です。</returns>
    private Task SetAsync(string key, JsonElement child, CancellationToken cancellationToken = default)
    {
        JsonObject value = Read();
        value[key] = JsonNode.Parse(child.GetRawText());
        return CommitAsync(value, cancellationToken);
    }

    /// <summary>一つの項目の型を変更します。</summary>
    /// <returns>項目順を維持した保存です。</returns>
    private Task PropertyAsync(string key, JsonElement child, CancellationToken cancellationToken = default)
    {
        JsonObject value = Read();
        JsonObject properties = Properties(value);
        properties[key] = JsonNode.Parse(child.GetRawText());
        return CommitAsync(value, cancellationToken);
    }

    /// <summary>重複していない名前で文字列項目を追加します。</summary>
    /// <returns>追加後の型です。</returns>
    private Task AddAsync(CancellationToken cancellationToken = default)
    {
        JsonObject value = Read();
        string propertiesKey = "properties";
        if (value[propertiesKey] is not JsonObject)
        {
            value["properties"] = new JsonObject();
        }
        JsonObject properties = Properties(value);
        int index = 1;
        string name = "field" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (properties.ContainsKey(name))
        {
            name = "field" + (++index).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        properties[name] = new JsonObject { ["type"] = "string" };
        return CommitAsync(value, cancellationToken);
    }

    /// <summary>名前変更で別の項目を上書きしません。</summary>
    /// <returns>重複エラーまたは順序を維持した変更です。</returns>
    private async Task RenameAsync(string oldName, string name, CancellationToken cancellationToken = default)
    {
        JsonObject value = Read();
        JsonObject original = Properties(value);
        if (!string.Equals(oldName, name, StringComparison.Ordinal) && original.ContainsKey(name))
        {
            problem = "Property names must be unique";
            await InvalidChanged.InvokeAsync(problem).ConfigureAwait(true);
            return;
        }
        JsonObject properties = new();
        foreach ((string key, JsonNode? child) in original)
        {
            string updated = string.Equals(key, oldName, StringComparison.Ordinal) ? name
                                                                                   : key;
            properties[updated] = child?.DeepClone();
        }
        value["properties"] = properties;
        await CommitAsync(value, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>指定項目だけを除去します。</summary>
    /// <returns>残る項目の保存です。</returns>
    private Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        JsonObject value = Read();
        Properties(value).Remove(name);
        return CommitAsync(value, cancellationToken);
    }

    /// <summary>変更を親へ渡し、無効な編集状態を解除します。</summary>
    /// <returns>設定と検証状態の通知です。</returns>
    private async Task CommitAsync(JsonObject value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        problem = null;
        await InvalidChanged.InvokeAsync(null).ConfigureAwait(true);
        await ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(value)).ConfigureAwait(true);
    }
}
