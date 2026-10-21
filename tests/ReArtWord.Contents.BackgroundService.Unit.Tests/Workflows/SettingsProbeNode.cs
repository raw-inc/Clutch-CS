using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実行された設定値と、固有検証の適用を観測します。</summary>
internal sealed class SettingsProbeNode : IActionNode
{
    /// <summary>必須数値・任意文字列・nullable設定を宣言します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("test.settings", "Settings probe", "Test",
        """{"type":"object","required":["port"],"properties":{"port":{"type":"integer","minimum":1,"maximum":100},"label":{"type":"string","enum":["original","wired"]},"optional":{"type":["string","null"]}}}""", inputs: []);

    /// <summary>スキーマに加えノード固有の条件を検証します。</summary>
    /// <returns>型・範囲または固有の禁止値です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        IReadOnlyList<ValidationIssue> schema = NodeSchemaValidator.Validate(definition, Descriptor);
        if (schema.Count != 0)
        {
            return schema;
        }
        string port = "port";
        if (definition.Settings.GetProperty(port).GetInt32() == 13)
        {
            return [new("settings.probe.reserved", "The node rejects this reserved value.", definition.Id)];
        }
        return [];
    }

    /// <summary>実際にNodeExecutorから渡された設定を返します。</summary>
    /// <returns>使用した設定オブジェクトです。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromJson(context.Settings)));
}
