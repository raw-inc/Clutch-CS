using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Resources;

namespace ReArtWord.Contents.BackgroundService.Nodes.Resources;

/// <summary>共通収集の実値・取得状態・履歴保存状態を一件返します。</summary>
internal sealed class ResourceObserveNode : IActionNode
{
    private readonly IResourceService service;

    /// <summary>Host共通の収集サービスを共有します。</summary>
    internal ResourceObserveNode(IResourceService service)
    {
        this.service = service;
    }

    /// <summary>実行するPeerを明示する現在値取得です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("resource.observe", "resource現在値", "Resources", """{"type":"object","properties":{}}""", [new PortDescriptor("value") { IsLegacy = true }], [new PortDescriptor("value"), new PortDescriptor("metrics")]) with { HasAsyncCompletion = true, Location = EExecutionLocation.ExplicitPeer };

    /// <summary>設定構造だけを確認します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>取得不能metricをゼロへ丸めず後続へ渡します。</summary>
    /// <returns>共通診断履歴に分岐した実測サンプルです。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        ResourceSample sample = await service.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return Result(sample);
    }

    /// <summary>契約JSONに従ってサンプルをノード値へ変換します。</summary>
    /// <returns>実測と欠損状態の一件です。</returns>
    internal static NodeResult Result(ResourceSample sample)
    {
        Dictionary<string, Dictionary<string, ResourceMetric>> metrics = sample.Metrics.GroupBy(metric => metric.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToDictionary(metric => metric.Target, StringComparer.Ordinal), StringComparer.Ordinal);
        return new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromJson(JsonSerializer.SerializeToElement(sample, ContractJson.Options)),
            ["metrics"] = NodeValue.FromJson(JsonSerializer.SerializeToElement(metrics, ContractJson.Options)),
        });
    }
}
