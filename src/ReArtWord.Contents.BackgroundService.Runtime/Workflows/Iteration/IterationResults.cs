using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>同じ要素で揃った結果の組だけを、各出力の入力順配列へ集約します。</summary>
internal sealed class IterationResults
{
    private readonly string[] ports;
    private readonly IReadOnlyDictionary<string, NodeValue>?[] rows;
    private readonly Dictionary<string, ValueTypeDefinition?> types;
    private readonly string duplicate;

    /// <summary>開始時の接続と要素数から、保持する出力だけを固定します。</summary>
    internal IterationResults(NodeDefinition end, WorkflowDefinition definition, int count)
    {
        ports = IterationResultPorts.Used(end, definition).ToArray();
        rows = new IReadOnlyDictionary<string, NodeValue>?[count];
        WorkflowTypeInference inference = new(definition, []);
        types = ports.ToDictionary(port => port, port => inference.PortType(end.Id, port, false), StringComparer.Ordinal);
        duplicate = new NodeSettings(end.Settings).ReadText("duplicate", "reject")!;
    }

    /// <summary>一部の値だけを別要素と合成せず、同じEndに届いた完全な組だけを選びます。</summary>
    /// <returns>全結果が揃った組、未到着があればnullです。</returns>
    internal IReadOnlyDictionary<string, NodeValue>? Read(NodeResult result)
    {
        if (result.Outcome != EOutcome.Succeeded || ports.Any(port => !result.Outputs.TryGetValue(port, out NodeValue? value) || value.Kind != EValueKind.Json))
        {
            return null;
        }
        return ports.ToDictionary(port => port, port => result.Outputs[port], StringComparer.Ordinal);
    }

    /// <summary>保持予算を確保できた一要素を入力添字に保存します。</summary>
    internal void Store(int index, IReadOnlyDictionary<string, NodeValue> values)
        => rows[index] = values.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal);

    /// <summary>省略した行は全出力で同時に除き、出力間の添字を一致させます。</summary>
    /// <returns>各値の型を保った、同じ長さの結果配列です。</returns>
    internal NodeResult Aggregate()
    {
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal);
        foreach (string port in ports)
        {
            NodeValue[] values = rows.Where(row => row is not null).Select(row => row![port]).ToArray();
            ValueTypeDefinition? type = types[port];
            bool dictionary = type?.KeyType is not null || values.Length != 0 && values.All(value => value.DataType?.KeyType is not null);
            if (dictionary)
            {
                try
                {
                    outputs[port] = DictionaryValues.Collect(values, duplicate, type?.KeyType ?? values[0].DataType!.KeyType!);
                }
                catch (FormatException error)
                {
                    return NodeResult.Failure("iteration." + error.Message, "The Key-Value results could not be collected into a dictionary.");
                }
            }
            else
            {
                outputs[port] = PathTypeMetadata.Apply(NodeValue.FromObject(values.Select(value => value.Json)), PathTypeMetadata.ArrayType(values.Select(value => value.DataType).ToArray()));
            }
        }
        return new(EOutcome.Succeeded, "success", outputs);
    }
}
