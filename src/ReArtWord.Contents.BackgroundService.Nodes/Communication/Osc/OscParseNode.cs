using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;

/// <summary>通信を開始せず、既存のOSC受信と同じ規則でパケットから値を取り出します。</summary>
public sealed class OscParseNode : IValueNode
{
    /// <summary>パケットObjectまたは生のOSCバイト列から型付きの複数出力を返します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(OscPorts.ParseNodeType, "Parse OSC packet", "Communication", """{"type":"object","properties":{}}""",
        inputs: [new("value", Required: true)], outputs: [new("value", EPortValueKind.Json), new("address", EPortValueKind.Json), new("arguments", EPortValueKind.Json)]) with
    {
        SettingsSchema = OscNodeValues.ArgumentSchema,
        Description = "Parse an OSC packet object or raw OSC bytes without network access. Use the same ordered argument types as OSC receivers to expose each argument. Types and counts must match; unset types preserve packet mode. Bundles retain their timetag and structure without flattening. Input is limited to 1 MiB.",
    };

    /// <summary>受信ノードと同じOSC型だけを構造設定に指定できます。</summary>
    /// <returns>共通スキーマとOSC引数型の検証結果です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. NodeSupport.Validate(definition, Descriptor), .. OscNodeValues.Validate(definition)];
    }

    /// <summary>実行用の一件も設定評価と同じ副作用のない解析へ渡します。</summary>
    /// <returns>解析結果または明示的な入力失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Evaluate(new(context.NodeId, Descriptor.Type, context.Settings, ContractVersion: context.ContractVersion), context.Inputs));
    }

    /// <summary>共通codecで境界・精度を検証してから受信と同じ出力へ展開します。</summary>
    /// <returns>不正な入力は部分出力を返しません。</returns>
    public NodeResult Evaluate(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputs);
        IReadOnlyList<ValidationIssue> issues = Validate(definition);
        if (issues.Count != 0)
        {
            return NodeResult.Failure(issues[0].Code, issues[0].Message, EOutcome.NotExecuted);
        }
        string valuePort = "value";
        if (!inputs.TryGetValue(valuePort, out NodeValue? input))
        {
            return NodeResult.Failure("osc.parse.required", "Supply an OSC packet object or raw OSC bytes.", EOutcome.NotExecuted);
        }
        try
        {
            ReadOnlySpan<byte> bytes;
            if (input.Kind == EValueKind.Binary)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Binary.Length, 1048576);
                bytes = input.Binary.Span;
            }
            else
            {
                if (input.Json.ValueKind != JsonValueKind.Object)
                {
                    return NodeResult.Failure("osc.parse.input", "Supply an OSC packet object or raw OSC bytes.");
                }
                ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Json.GetRawText().Length, 1048576);

                // Objectでも、受信時と同じwire精度・境界検証を経て共通の解析へ渡します。
                bytes = OscPacketCodec.Encode(input.Json);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, 1048576);
            }
            return OscNodeValues.Decode(bytes, definition.Settings) ?? NodeResult.Failure("osc.parse.input", "Supply an OSC packet object or raw OSC bytes.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return NodeResult.Failure("osc.parse.invalid", "The OSC packet is malformed, unsupported or exceeds the 1 MiB limit.");
        }
    }
}
