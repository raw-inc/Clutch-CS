using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;

/// <summary>ノードと接続の識別を保って三者の編集を比較します。</summary>
public static class WorkflowEditorMerge
{
    /// <summary>非競合項目だけを自動統合します。</summary>
    /// <returns>統合定義と未解消項目。</returns>
    public static WorkflowMergeResult Merge(WorkflowDefinition original, WorkflowDefinition current, WorkflowMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        if (original.IsDashboard != current.IsDashboard || current.IsDashboard != request.Edited.IsDashboard)
        {
            return new(null, [], [new("workflow.kind", "WFDashboard and Workflow cannot be converted into each other.")], null);
        }
        if (new[] { original, current, request.Edited }.Any(definition => definition.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != definition.Nodes.Count || definition.Wires.Select(wire => (wire.FromNode, wire.FromPort, wire.ToNode, wire.ToPort)).Distinct().Count() != definition.Wires.Count))
        {
            return new WorkflowMergeResult(null, [], [new ValidationIssue("merge.identityCollision", "同じノード識別子または接続が重複しています。")], null);
        }
        if (request.Resolutions.TryGetValue(string.Empty, out EEditResolution whole) && whole != EEditResolution.Cancel)
        {
            WorkflowDefinition chosen = current;
            if (whole == EEditResolution.UseEdited)
            {
                chosen = request.Edited;
            }
            return new WorkflowMergeResult(ContractJson.Copy(chosen), [], [], null);
        }

        // 同じ内容のACKでは巨大な三つの比較木を作らず、識別子検証後に所有コピーだけを返します。
        string originalHash = ContractJson.Hash(original), currentHash = ContractJson.Hash(current);
        if (string.Equals(originalHash, currentHash, StringComparison.Ordinal))
        {
            return new WorkflowMergeResult(ContractJson.Copy(request.Edited), [], [], null);
        }
        string editedHash = ContractJson.Hash(request.Edited);
        if (string.Equals(originalHash, editedHash, StringComparison.Ordinal) || string.Equals(currentHash, editedHash, StringComparison.Ordinal))
        {
            return new WorkflowMergeResult(ContractJson.Copy(current), [], [], null);
        }
        JsonObject originalData = Normalize(original);
        JsonObject currentData = Normalize(current);
        JsonObject editedData = Normalize(request.Edited);
        if (JsonNode.DeepEquals(originalData, currentData) && JsonNode.DeepEquals(originalData, editedData))
        {
            return new WorkflowMergeResult(ContractJson.Copy(original), [], [], null);
        }
        if (JsonNode.DeepEquals(originalData, editedData) || JsonNode.DeepEquals(currentData, editedData))
        {
            return new WorkflowMergeResult(ContractJson.Copy(current), [], [], null);
        }
        if (JsonNode.DeepEquals(originalData, currentData))
        {
            return new WorkflowMergeResult(ContractJson.Copy(request.Edited), [], [], null);
        }
        List<EditConflict> conflicts = [];
        EditValue merged = MergeValue(new EditValue(true, originalData), new EditValue(true, currentData), new EditValue(true, editedData), string.Empty, request.Resolutions, conflicts);
        WorkflowDefinition? definition = null;
        if (merged.Data is JsonObject document)
        {
            RestoreArray(document, "nodes");
            RestoreArray(document, "wires");
            definition = document.Deserialize<WorkflowDefinition>(ContractJson.Options);
        }
        return new WorkflowMergeResult(definition, conflicts, [], null);
    }

    /// <summary>配列の位置変化を同じノードの競合と誤認しない形式へ変換します。</summary>
    /// <returns>識別子辞書を持つ文書。</returns>
    private static JsonObject Normalize(WorkflowDefinition definition)
    {
        JsonObject document = JsonSerializer.SerializeToNode(definition, ContractJson.Options)!.AsObject();
        JsonObject nodes = new();
        foreach (NodeDefinition node in definition.Nodes)
        {
            nodes[node.Id] = JsonSerializer.SerializeToNode(node, ContractJson.Options);
        }
        JsonObject wires = new();
        foreach (WireDefinition wire in definition.Wires)
        {
            string key = JsonSerializer.Serialize(new[] { wire.FromNode, wire.FromPort, wire.ToNode, wire.ToPort });
            wires[key] = JsonSerializer.SerializeToNode(wire, ContractJson.Options);
        }
        document["nodes"] = nodes;
        document["wires"] = wires;
        return document;
    }

    /// <summary>実行定義へ戻す配列を構築します。</summary>
    private static void RestoreArray(JsonObject document, string name)
    {
        JsonObject values = document[name]!.AsObject();
        document[name] = new JsonArray(values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value?.DeepClone()).ToArray());
    }

    /// <summary>同じ項目への異なる変更だけを人の選択へ残します。</summary>
    /// <returns>項目の統合候補。</returns>
    private static EditValue MergeValue(EditValue original, EditValue current, EditValue edited, string path, IReadOnlyDictionary<string, EEditResolution> resolutions, List<EditConflict> conflicts)
    {
        if (Same(original, edited) || Same(current, edited))
        {
            return current.Copy();
        }
        if (Same(original, current))
        {
            return edited.Copy();
        }
        if (original.Data is JsonObject originalObject && current.Data is JsonObject currentObject && edited.Data is JsonObject editedObject)
        {
            JsonObject result = new();
            foreach (string key in originalObject.Select(pair => pair.Key).Concat(currentObject.Select(pair => pair.Key)).Concat(editedObject.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal))
            {
                string child = path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                EditValue value = MergeValue(new EditValue(originalObject.ContainsKey(key), originalObject[key]), new EditValue(currentObject.ContainsKey(key), currentObject[key]), new EditValue(editedObject.ContainsKey(key), editedObject[key]), child, resolutions, conflicts);
                if (value.Exists)
                {
                    result[key] = value.Data;
                }
            }
            return new EditValue(true, result);
        }
        if (resolutions.TryGetValue(path, out EEditResolution resolution))
        {
            if (resolution == EEditResolution.KeepCurrent)
            {
                return current.Copy();
            }
            if (resolution == EEditResolution.UseEdited)
            {
                return edited.Copy();
            }
        }
        conflicts.Add(new EditConflict(path, ToElement(original), ToElement(current), ToElement(edited), original.Exists, current.Exists, edited.Exists));
        return current.Copy();
    }

    /// <summary>表示用の候補値を所有権を分けて保持します。</summary>
    /// <returns>項目値。</returns>
    private static JsonElement? ToElement(EditValue value)
    {
        if (!value.Exists)
        {
            return null;
        }
        return JsonSerializer.SerializeToElement(value.Data, ContractJson.Options);
    }

    /// <summary>存在状態を含めて編集差分を比較します。</summary>
    /// <returns>同じ項目状態か。</returns>
    private static bool Same(EditValue first, EditValue second) => first.Exists == second.Exists && JsonNode.DeepEquals(first.Data, second.Data);
}
