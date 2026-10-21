using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>ノード自身の挙動を外部契約から試験する文脈を作ります。</summary>
internal static class NodeTestContext
{
    /// <summary>実行一件と入力済み入力を作成します。</summary>
    /// <returns>独立した実行文脈です。</returns>
    internal static NodeExecutionContext Create(string settings = "{}", NodeValue? value = null, IReadOnlyDictionary<string, NodeValue>? extra = null, TimeProvider? time = null)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (value is not null)
        {
            inputs["value"] = value;
        }
        if (extra is not null)
        {
            foreach (KeyValuePair<string, NodeValue> item in extra)
            {
                inputs[item.Key] = item.Value;
            }
        }
        return new NodeExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "workflow", "r1", "node", "peer", NodeSettings.Schema(settings), inputs, time ?? TimeProvider.System);
    }
}
