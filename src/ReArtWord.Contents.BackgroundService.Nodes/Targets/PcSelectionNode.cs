using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Targets;

/// <summary>選択したPCを保存可能な条件として出力します。参加状態は実行前に確認します。</summary>
public sealed class PcSelectionNode : PureValueNode
{
    /// <summary>PC一覧と全参加PCの選択を公開します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(PcSelectionPorts.SelectNode, "Select PCs", "Execution",
        """{"type":"object","required":["mode"],"properties":{"mode":{"type":"string","enum":["selected","all"]},"hostIds":{"type":"array","maxItems":128,"items":{"type":"string","minLength":1},"x-visibleWhen":{"property":"mode","values":["selected"]},"x-requiredWhen":{"property":"mode","values":["selected"]}}}}""",
        inputs: [], outputs: [new("value", EPortValueKind.PcSelection)]) with
    {
        Description = "Choose PCs in this cluster. Connect several selections to an execution PC input to combine their candidates.",
    };

    /// <summary>候補が空の場合も、未指定とは異なる明示選択として保持します。</summary>
    /// <returns>実行時に所属と実体を照合する選択条件。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        NodeSettings settings = new(definition.Settings);
        bool all = string.Equals(settings.ReadText("mode"), "all", StringComparison.Ordinal);
        PcSelection selection;
        if (all)
        {
            selection = new(AllHosts: true);
        }
        else
        {
            System.Text.Json.JsonElement hosts = settings.Get("hostIds");
            if (hosts.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new FormatException("pcListRequired");
            }
            selection = new(HostIds: hosts.EnumerateArray().Select(host => host.GetString()!).Distinct(StringComparer.Ordinal).ToArray());
        }
        return NodeValue.FromObject(new[] { selection });
    }
}
