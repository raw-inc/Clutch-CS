using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>競合候補の比較・編集と解消要求の組立を担当します。</summary>
public sealed partial class ConflictPanel
{
    /// <summary>候補の種類と内容に応じて直接編集できるかを判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    internal static bool CanEdit(StateConflict conflict, StateConflictCandidate? candidate) => conflict.Resolvable && !conflict.ValuesHidden && candidate?.Value?.Kind == EValueKind.Json && (conflict.Scope is "workflows" or "workflow.activation" or "cluster.operational" or "cluster.operational.activation" or "cluster.operational.history" or "state:cluster" || conflict.Scope.StartsWith("workflow:", StringComparison.Ordinal) || conflict.Scope.StartsWith("workflow.history:", StringComparison.Ordinal));

    /// <summary>選択した候補と基準版から競合解消要求を組み立てます。</summary>
    /// <returns>対象の版と採用する内容を固定した競合解消要求です。</returns>
    internal static ConflictResolveRequest BuildRequest(StateConflict conflict, string? selectedVersion, bool reviewed, bool useEdit, string raw)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (!reviewed || !conflict.Resolvable || conflict.Candidates.Count == 0)
        {
            throw UiError.InvalidOperation("Review all candidates.");
        }

        StateConflictCandidate? candidate = conflict.Candidates.SingleOrDefault(item => string.Equals(item.Version, selectedVersion, StringComparison.Ordinal)) ?? throw UiError.InvalidOperation("Select a candidate revision.");
        NodeValue? value = null;
        if (useEdit)
        {
            if (!CanEdit(conflict, candidate) || 262144 < raw.Length)
            {
                throw UiError.InvalidOperation("This candidate or its size cannot be edited.");
            }

            using JsonDocument? document = JsonDocument.Parse(raw);
            value = Rebuild(conflict, candidate, document.RootElement);
        }

        return new(Guid.NewGuid(), conflict.Id, conflict.Candidates.Select(item => item.Version).Distinct(StringComparer.Ordinal).ToArray(), useEdit ? null
                                                                                                                                                     : selectedVersion, value, useEdit);
    }

    /// <summary>比較する内容を表示用の文字列へ変換します。</summary>
    /// <returns>型と欠損状態を区別した比較用の表示です。</returns>
    private string Preview(NodeValue? value)
    {
        if (value is null)
        {
            return Ui["Deletion candidate"];
        }

        if (value.Kind == EValueKind.Binary)
        {
            return Ui.Format("Binary data: {0} bytes (select a candidate revision)", value.Binary.Length);
        }

        string json = value.Json.GetRawText();
        return json.Length <= 4096 ? json
                                   : json[..4096] + "\n…" + Ui["Display limited to 4096 characters. Selection uses the complete saved value."];
    }

    /// <summary>候補の保存形式から編集可能な内容を取り出します。</summary>
    /// <returns>運用設定または内部値から抽出した編集対象のJSONです。</returns>
    private static JsonElement EditableContent(StateConflict conflict, StateConflictCandidate candidate)
    {
        JsonElement value = candidate.Value!.Json;
        if (IsWorkflow(conflict.Scope))
        {
            return JsonSerializer.SerializeToElement(value.Deserialize<StoredWorkflow>(ContractJson.Options)!.Definition, ContractJson.Options);
        }

        if (conflict.Scope.StartsWith(MatchClusterOperational, StringComparison.Ordinal))
        {
            return JsonSerializer.SerializeToElement(Snapshot(conflict.Scope, value).Settings, ContractJson.Options);
        }

        return value;
    }

    /// <summary>候補の識別情報を維持して編集済み内容を保存形式へ戻します。</summary>
    /// <returns>元の識別情報と編集した内容を持つ保存候補です。</returns>
    private static NodeValue Rebuild(StateConflict conflict, StateConflictCandidate candidate, JsonElement editedValue)
    {
        if (IsWorkflow(conflict.Scope))
        {
            StoredWorkflow? original = candidate.Value!.Json.Deserialize<StoredWorkflow>(ContractJson.Options)!;
            WorkflowDefinition? definition = editedValue.Deserialize<WorkflowDefinition>(ContractJson.Options) ?? throw UiError.Attach(new JsonException("A definition is required."), "A definition is required.");
            if (!string.Equals(definition.Id, original.Definition.Id, StringComparison.Ordinal))
            {
                throw UiError.InvalidOperation("The workflow ID cannot be changed.");
            }

            return NodeValue.FromObject(original with
            {
                Definition = definition,
                Revision = ContractJson.Hash(definition)
            });
        }

        if (conflict.Scope.StartsWith(MatchClusterOperational, StringComparison.Ordinal))
        {
            ClusterOperationalSnapshot? snapshot = Snapshot(conflict.Scope, candidate.Value!.Json);
            ClusterOperationalSettings? settings = editedValue.Deserialize<ClusterOperationalSettings>(ContractJson.Options) ?? throw UiError.Attach(new JsonException("Operational settings are required."), "Operational settings are required.");
            ClusterOperationalSnapshot? changed = snapshot with
            {
                Settings = settings,
                Revision = ContractJson.Hash(settings)
            };
            if (string.Equals(conflict.Scope, MatchClusterOperationalActivation, StringComparison.Ordinal))
            {
                return NodeValue.FromObject(changed);
            }

            JsonObject? wrapper = JsonNode.Parse(candidate.Value!.Json.GetRawText())!.AsObject();
            string key = wrapper.Select(item => item.Key).Single(key => key.Equals("snapshot", StringComparison.OrdinalIgnoreCase));
            wrapper[key] = JsonSerializer.SerializeToNode(changed, ContractJson.Options);
            return NodeValue.FromJson(JsonSerializer.SerializeToElement(wrapper, ContractJson.Options));
        }

        return NodeValue.FromJson(editedValue);
    }

    /// <summary>保存範囲がワークフローの定義または反映版に属するかを判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private static bool IsWorkflow(string scope) => scope is "workflows" or "workflow.activation" || scope.StartsWith("workflow.history:", StringComparison.Ordinal);

    /// <summary>保存範囲に応じた運用設定スナップショットを読み取ります。</summary>
    /// <returns>対象範囲の契約に従って復号した設定スナップショットです。</returns>
    private static ClusterOperationalSnapshot Snapshot(string scope, JsonElement value)
    {
        if (string.Equals(scope, MatchClusterOperationalActivation, StringComparison.Ordinal))
        {
            return value.Deserialize<ClusterOperationalSnapshot>(ContractJson.Options)!;
        }

        return value.EnumerateObject().Single(item => item.Name.Equals("snapshot", StringComparison.OrdinalIgnoreCase)).Value.Deserialize<ClusterOperationalSnapshot>(ContractJson.Options)!;
    }
}
