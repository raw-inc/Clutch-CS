using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>画面所有の定義を比較し、共同編集の表示順の差を内容変更と区別します。</summary>
internal static class WorkflowDraftOrder
{
    private static readonly ConditionalWeakTable<WorkflowDefinition, Fingerprint> revisions = new();
    private static readonly ConditionalWeakTable<WorkflowDefinition, Fingerprint> ordered = new();

    /// <summary>編集時に置き換える画面所有の定義について、同じ版を再計算しません。</summary>
    /// <returns>保存契約と同じ内容識別子です。定義の寿命を延ばしません。</returns>
    internal static string Revision(WorkflowDefinition definition) => revisions.GetValue(definition, static value => new(ContractJson.Hash(value))).Value;

    /// <summary>版識別子に対応する原文を変更せず、表示順だけの差を比較から除きます。</summary>
    /// <returns>ノードと配線の並び以外が一致するかです。</returns>
    internal static bool Same(WorkflowDefinition first, WorkflowDefinition second) => ReferenceEquals(first, second)
        || string.Equals(UnorderedRevision(first), UnorderedRevision(second), StringComparison.Ordinal);

    /// <summary>ノードと接続の順序だけを揃えた比較結果を、同じ所有定義の間で再利用します。</summary>
    /// <returns>値や数値のJSON表現を変更しない比較用の識別子です。</returns>
    private static string UnorderedRevision(WorkflowDefinition definition) => ordered.GetValue(definition, static value => new(ContractJson.Hash(value with
    {
        Nodes = value.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
        Wires = value.Wires.OrderBy(wire => wire.FromNode, StringComparer.Ordinal).ThenBy(wire => wire.FromPort, StringComparer.Ordinal)
            .ThenBy(wire => wire.ToNode, StringComparer.Ordinal).ThenBy(wire => wire.ToPort, StringComparer.Ordinal).ToArray(),
    }))).Value;

    /// <summary>所有定義と一緒に回収する比較結果です。</summary>
    private sealed record Fingerprint(string Value);

}
