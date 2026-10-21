using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>人が選んだ値だけをJSON Pointerへ適用します。</summary>
internal static class ConflictSelection
{
    /// <summary>削除と明示nullを区別して一つの候補を採用します。</summary>
    /// <returns>元の値を共有しない編集候補です。</returns>
    public static WorkflowDefinition Apply(WorkflowDefinition document, IReadOnlyList<EditConflict> conflicts, IReadOnlyDictionary<string, int> choices)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(choices);
        EditConflict? whole = conflicts.FirstOrDefault(item => item.Path.Length == 0);
        if (whole is not null)
        {
            if (!choices.TryGetValue(string.Empty, out int selection))
            {
                throw UiError.InvalidOperation("Choose which value to keep for every conflict.");
            }
            if (selection == -1)
            {
                return ContractJson.Copy(document);
            }
            EditConflict chosen = conflicts[selection];
            if (chosen.Path.Length != 0 || chosen.EditedExists != true || chosen.Edited is null)
            {
                throw UiError.InvalidOperation("The candidates have changed. Reopen the comparison.");
            }
            return chosen.Edited.Value.Deserialize<WorkflowDefinition>(ContractJson.Options)!;
        }
        JsonObject normalized = JsonSerializer.SerializeToNode(document, ContractJson.Options)!.AsObject();
        normalized["nodes"] = new JsonObject(document.Nodes.Select(node => KeyValuePair.Create<string, JsonNode?>(node.Id, JsonSerializer.SerializeToNode(node, ContractJson.Options))));
        normalized["wires"] = new JsonObject(document.Wires.Select(wire => KeyValuePair.Create<string, JsonNode?>(JsonSerializer.Serialize(new[] { wire.FromNode, wire.FromPort, wire.ToNode, wire.ToPort }), JsonSerializer.SerializeToNode(wire, ContractJson.Options))));
        foreach (string path in conflicts.Select(conflict => conflict.Path).Distinct(StringComparer.Ordinal))
        {
            if (!choices.TryGetValue(path, out int choice))
            {
                throw UiError.Attach(new InvalidOperationException("Choose which value to keep for every conflict."), "Choose which value to keep for every conflict.");
            }
            if (choice == -1)
            {
                continue;
            }
            EditConflict selected = conflicts[choice];
            if (!string.Equals(selected.Path, path, StringComparison.Ordinal) || selected.Edited is null && selected.EditedExists is null)
            {
                throw UiError.Attach(new InvalidOperationException("The candidate does not distinguish deletion from null. Reload the comparison."), "The candidate does not distinguish deletion from null. Reload the comparison.");
            }
            string[] segments = path[1..].Split('/').Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            JsonObject parent = normalized;
            foreach (string segment in segments[..^1])
            {
                if (parent[segment] is not JsonObject child)
                {
                    child = [];
                    parent[segment] = child;
                }
                parent = child;
            }
            if (selected.EditedExists == false)
            {
                parent.Remove(segments[^1]);
            }
            else
            {
                parent[segments[^1]] = selected.Edited is JsonElement value ? JsonNode.Parse(value.GetRawText())
                                                                            : null;
            }
        }
        foreach (string key in new[] { "nodes", "wires" })
        {
            JsonArray values = [..normalized[key]!.AsObject().Select(pair => pair.Value?.DeepClone())];
            normalized[key] = values;
        }
        return normalized.Deserialize<WorkflowDefinition>(ContractJson.Options)!;
    }
}
