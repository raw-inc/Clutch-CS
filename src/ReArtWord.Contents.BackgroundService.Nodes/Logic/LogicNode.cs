using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Logic;

/// <summary>JSON真偽値または整数へ単項・二項・配列の論理演算を適用します。</summary>
public sealed class LogicNode : IRuntimeValueNode
{
    private readonly string operation;

    /// <summary>論理演算と対応する入力を固定します。</summary>
    public LogicNode(string operation)
    {
        this.operation = operation switch
        {
            "and" or "or" or "not" or "nand" or "nor" or "xor" or "ands" or "ors" => operation,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        PortDescriptor[] inputs = operation switch
        {
            "not" => [new("value", EPortValueKind.Any, true)],
            "ands" or "ors" => [new("value", EPortValueKind.Json, true)],
            _ => [new("left", EPortValueKind.Any, true), new("right", EPortValueKind.Any, true)],
        };
        Descriptor = NodeSupport.Describe("math." + operation, operation, "Math", """{"type":"object","properties":{}}""",
            inputs: inputs,
            outputs: [new PortDescriptor("value", EPortValueKind.Json)]) with
        {
            IsValueOperation = true,
            Description = operation switch
            {
                "not" => "Invert a JSON boolean or complement an exact Int64 JSON integer. Implicit conversion fails.",
                "ands" or "ors" => "Combine an array of 2 to 4096 JSON booleans or exact Int64 JSON integers. Mixed types and implicit conversion fail.",
                _ => "Apply Boolean or bitwise logic to two required JSON booleans or exact Int64 JSON integers. Mixed types and implicit conversion fail.",
            },
        };
    }

    /// <summary>演算に応じた入力と真偽値または整数の出力を宣言します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>設定を共通validatorで検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>全入力の型を確認して論理演算または整数ビット演算を適用します。</summary>
    /// <returns>JSON真偽値・整数または入力不正の失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        List<NodeValue> operands = [];
        NodeResult? inputError = CollectInputs(context, operands);
        if (inputError is not null)
        {
            return ValueTask.FromResult(inputError);
        }
        try
        {
            return ValueTask.FromResult(Calculate(operands));
        }
        catch (FormatException error)
        {
            return ValueTask.FromResult(NodeResult.Failure("math.input." + error.Message, "Numeric operands must be exact Int64 JSON integers."));
        }
    }

    /// <summary>宣言した必須入力を収集し、配列演算では列挙前に長さを制限します。</summary>
    /// <returns>入力形状の失敗、または有効ならnullです。</returns>
    private NodeResult? CollectInputs(NodeExecutionContext context, List<NodeValue> operands)
    {
        foreach (PortDescriptor port in Descriptor.Inputs)
        {
            if (!context.Inputs.TryGetValue(port.Name, out NodeValue? operand))
            {
                return NodeResult.Failure("math.input.missing", "All declared inputs are required.");
            }
            operands.Add(operand);
        }
        bool isArray = operation is "ands" or "ors";
        if (isArray)
        {
            NodeValue values = operands[0];
            if (values.Kind != EValueKind.Json || values.Json.ValueKind != JsonValueKind.Array)
            {
                return NodeResult.Failure("math.input.array.type", "The value input must be a JSON array.");
            }
            int count = values.Json.GetArrayLength();
            if (count < 2 || 4096 < count)
            {
                return NodeResult.Failure("math.input.array.range", "The value array must contain 2 to 4096 operands.");
            }
            operands.Clear();
            foreach (JsonElement element in values.Json.EnumerateArray())
            {
                operands.Add(new NodeValue(EValueKind.Json, element, default));
            }
        }
        return null;
    }

    /// <summary>同種の真偽値または整数を全件検証しながら集約します。</summary>
    /// <returns>型に対応した結果または混在・型不正の失敗です。</returns>
    private NodeResult Calculate(List<NodeValue> operands)
    {
        bool booleanMode = operands[0].Kind == EValueKind.Json && operands[0].Json.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool isAnd = operation is "and" or "nand" or "ands";
        bool booleanResult = isAnd;
        long integerResult = -1;
        if (!isAnd)
        {
            integerResult = 0;
        }
        foreach (NodeValue operand in operands)
        {
            if (operand.Kind != EValueKind.Json)
            {
                return TypeFailure();
            }
            if (booleanMode)
            {
                if (operand.Json.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return TypeFailure();
                }
                booleanResult = operation switch
                {
                    "and" or "nand" or "ands" => booleanResult & operand.Json.GetBoolean(),
                    "xor" => booleanResult ^ operand.Json.GetBoolean(),
                    _ => booleanResult | operand.Json.GetBoolean(),
                };
            }
            else
            {
                if (operand.Json.ValueKind != JsonValueKind.Number)
                {
                    return TypeFailure();
                }
                _ = ScalarNumbers.Read(operand);
                long integer = ScalarNumbers.Integer(ScalarNumbers.Token(operand));
                integerResult = operation switch
                {
                    "and" or "nand" or "ands" => integerResult & integer,
                    "xor" => integerResult ^ integer,
                    _ => integerResult | integer,
                };
            }
        }
        bool negate = operation is "not" or "nand" or "nor";
        if (negate)
        {
            booleanResult = !booleanResult;
            integerResult = ~integerResult;
        }
        if (booleanMode)
        {
            return NodeResult.Success(NodeValue.FromObject(booleanResult));
        }
        return NodeResult.Success(NodeValue.FromObject(integerResult));
    }

    /// <summary>同種の真偽値または整数だけを受理する入力エラーを作ります。</summary>
    /// <returns>混在または不正型の失敗です。</returns>
    private static NodeResult TypeFailure() => NodeResult.Failure("math.input.logic.type", "Operands must be all JSON booleans or all exact Int64 JSON integers.");
}
