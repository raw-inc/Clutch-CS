using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Comparison;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>順序と値の型を保持する有限の集合変換です。</summary>
internal static class CollectionOperations
{
    /// <summary>明示したキー比較で結合し、一致しない外部結合行はnullで表します。</summary>
    /// <returns>left/rightを別項目に持つ結果行です。</returns>
    internal static JsonArray Join(JsonElement left, JsonElement right, NodeSettings settings, CollectionBudget budget)
    {
        JsonElement[] second = right.EnumerateArray().ToArray();
        bool[] used = new bool[second.Length];
        JsonArray output = [];
        string kind = settings.ReadText("kind", "inner")!;
        string comparison = settings.ReadText("comparison", "equal")!;
        bool matchNull = settings.ReadBoolean("matchNull");
        bool keepLeft = kind switch { "left" or "full" => true, _ => false };
        bool keepRight = kind switch { "right" or "full" => true, _ => false };
        foreach (JsonElement first in left.EnumerateArray())
        {
            bool found = false;
            for (int index = 0; index < second.Length; index++)
            {
                budget.Step();
                NodeValue a = ExtractNode.Pointer(NodeValue.FromJson(first), settings.ReadText("leftPath", "")!);
                NodeValue b = ExtractNode.Pointer(NodeValue.FromJson(second[index]), settings.ReadText("rightPath", "")!);
                if (!matchNull && (a.Json.ValueKind == JsonValueKind.Null || b.Json.ValueKind == JsonValueKind.Null))
                {
                    continue;
                }
                if (ValueComparison.Test(a, b, comparison) == true)
                {
                    budget.Add(output, Pair(first, second[index]));
                    found = true;
                    used[index] = true;
                }
            }
            if (!found && keepLeft)
            {
                budget.Add(output, Pair(first, null));
            }
        }
        if (keepRight)
        {
            for (int index = 0; index < second.Length; index++)
            {
                if (!used[index])
                {
                    budget.Add(output, Pair(null, second[index]));
                }
            }
        }
        return output;
    }

    /// <summary>キーの初出順と各組内の入力順を維持します。</summary>
    /// <returns>key/itemsからなる組の配列です。</returns>
    internal static JsonArray Group(JsonElement input, string path, CollectionBudget budget)
    {
        List<(NodeValue Key, List<JsonElement> Items)> groups = [];
        foreach (JsonElement item in input.EnumerateArray())
        {
            NodeValue key = ExtractNode.Pointer(NodeValue.FromJson(item), path);
            int match = -1;
            for (int index = 0; index < groups.Count; index++)
            {
                budget.Step();
                if (ValueComparison.Equal(key, groups[index].Key))
                {
                    match = index;
                    break;
                }
            }
            if (match < 0)
            {
                groups.Add((key, [item]));
            }
            else
            {
                groups[match].Items.Add(item);
            }
        }
        JsonArray output = [];
        foreach ((NodeValue key, List<JsonElement> items) in groups)
        {
            JsonArray values = new(items.Select(item => JsonNode.Parse(item.GetRawText())).ToArray());
            budget.Add(output, new JsonObject { ["key"] = JsonNode.Parse(key.Json.GetRawText()), ["items"] = values });
        }
        return output;
    }

    /// <summary>指定深さまで配列だけを展開し、スカラーとnullは保持します。</summary>
    /// <returns>入力順の平坦化結果です。</returns>
    internal static JsonArray Flatten(JsonElement input, int depth, CollectionBudget budget)
    {
        JsonArray output = [];
        foreach (JsonElement item in input.EnumerateArray())
        {
            FlattenItem(item, depth, output, budget);
        }
        return output;
    }

    /// <summary>一要素の配列を展開し、同じ出力予算へ追加します。</summary>
    private static void FlattenItem(JsonElement item, int depth, JsonArray output, CollectionBudget budget)
    {
        budget.Step();
        if (0 < depth && item.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement nested in item.EnumerateArray())
            {
                FlattenItem(nested, depth - 1, output, budget);
            }
        }
        else
        {
            budget.Add(output, JsonNode.Parse(item.GetRawText()));
        }
    }

    /// <summary>各objectの指定配列項目を一要素ずつ持つ行へ展開します。</summary>
    /// <returns>元objectの他の項目を保った行です。</returns>
    internal static JsonArray Expand(JsonElement input, string field, bool keepEmpty, CollectionBudget budget)
    {
        JsonArray output = [];
        foreach (JsonElement item in input.EnumerateArray())
        {
            budget.Step();
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(field, out JsonElement collection) || collection.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("collection.expandArrayRequired");
            }
            if (collection.GetArrayLength() == 0 && keepEmpty)
            {
                budget.Add(output, JsonNode.Parse(item.GetRawText()));
            }
            foreach (JsonElement element in collection.EnumerateArray())
            {
                JsonObject row = JsonNode.Parse(item.GetRawText())!.AsObject();
                row[field] = JsonNode.Parse(element.GetRawText());
                budget.Add(output, row);
            }
        }
        return output;
    }

    /// <summary>比較不能を不一致へ丸めず、独立した出力集合に残します。</summary>
    /// <returns>成立・不成立・判定不能の配列です。</returns>
    internal static Dictionary<string, NodeValue> Split(JsonElement input, NodeValue expected, NodeSettings settings, CollectionBudget budget)
    {
        JsonArray matched = [], rejected = [], unknown = [];
        foreach (JsonElement item in input.EnumerateArray())
        {
            budget.Step();
            bool? result;
            try
            {
                result = ValueComparison.Test(ExtractNode.Pointer(NodeValue.FromJson(item), settings.ReadText("path", "")!), expected, settings.ReadText("comparison", "equal")!);
            }
            catch (FormatException)
            {
                result = null;
            }
            JsonArray target = unknown;
            if (result == true)
            {
                target = matched;
            }
            if (result == false)
            {
                target = rejected;
            }
            budget.Add(target, JsonNode.Parse(item.GetRawText()));
        }
        return new(StringComparer.Ordinal) { ["matched"] = NodeValue.FromObject(matched), ["rejected"] = NodeValue.FromObject(rejected), ["indeterminate"] = NodeValue.FromObject(unknown) };
    }

    /// <summary>左右のキー衝突を避けて結合した値を構成します。</summary>
    /// <returns>存在しない側を明示nullにした行です。</returns>
    private static JsonObject Pair(JsonElement? left, JsonElement? right)
        => new() { ["left"] = JsonSerializer.SerializeToNode(left), ["right"] = JsonSerializer.SerializeToNode(right) };
}
