using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>表示中の一文書だけの検索索引を保持し、バイナリーや実行値を文字列化しません。</summary>
internal sealed class EditorNodeSearch
{
    private WorkflowDefinition? document;

    // 抑制理由: 比較元の不変スナップショット参照を交換し、元のコレクションを変更しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "検索元の不変スナップショットを保持します。")]
    private IReadOnlyDictionary<string, string>? inputs;
    private string? language;

    // 抑制理由: 比較元の不変スナップショット参照を交換し、元のコレクションを変更しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "検索元の不変スナップショットを保持します。")]
    private NodeDescriptor[] descriptors = [];
    private readonly List<(string Id, string[] Values)> entries = [];

    /// <summary>別WFへ移ったときは、以前の検索本文を保持しません。</summary>
    internal void Clear()
    {
        document = null;
        inputs = null;
        language = null;
        descriptors = [];
        entries.Clear();
    }

    /// <summary>ノード名・識別子・設定値・定数入力と未確定入力から、ノード単位の一致を返します。</summary>
    /// <returns>文書順に重複しないノードIDです。</returns>
    internal IReadOnlyList<string> Find(EditorDraft draft, IReadOnlyList<NodeDescriptor> catalog, UiText text, string query)
    {
        string needle = query.Trim();
        if (needle.Length == 0)
        {
            return [];
        }
        if (!ReferenceEquals(document, draft.Document) || !ReferenceEquals(inputs, draft.InputDrafts)
            || !string.Equals(language, text.Language, StringComparison.Ordinal) || !descriptors.SequenceEqual(catalog))
        {
            document = draft.Document;
            inputs = draft.InputDrafts;
            language = text.Language;
            descriptors = catalog.ToArray();
            entries.Clear();
            Dictionary<string, NodeDescriptor> types = catalog.ToDictionary(item => item.Type, StringComparer.Ordinal);
            foreach (NodeDefinition node in document.Nodes)
            {
                List<string> values = [node.Id, node.Type];
                if (!string.IsNullOrWhiteSpace(node.LogicalName))
                {
                    values.Add(node.LogicalName);
                }
                if (types.TryGetValue(node.Type, out NodeDescriptor? descriptor))
                {
                    values.Add(text.NodeName(descriptor));
                    values.Add(descriptor.DisplayName);
                }
                Collect(node.Settings, values);
                foreach (NodeValue value in node.Inputs?.Values ?? [])
                {
                    if (value.Kind == EValueKind.Json)
                    {
                        Collect(value.Json, values);
                    }
                }
                if (inputs is not null)
                {
                    values.AddRange(inputs.Where(pair => pair.Key.StartsWith(node.Id + "/", StringComparison.Ordinal)).Select(pair => pair.Value));
                }
                entries.Add((node.Id, values.ToArray()));
            }
        }
        return entries.Where(entry => entry.Values.Any(value => value.Contains(needle, StringComparison.OrdinalIgnoreCase))).Select(entry => entry.Id).ToArray();
    }

    /// <summary>表示可能な葉の値だけを索引に含め、JSONの全体コピーを重ねません。</summary>
    private static void Collect(JsonElement value, List<string> values)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    Collect(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    Collect(item, values);
                }
                break;
            case JsonValueKind.String:
                values.Add(value.GetString()!);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                values.Add(value.GetRawText());
                break;
            default:
                break;
        }
    }
}
