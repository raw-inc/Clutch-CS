using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;

/// <summary>JSON数値を暗黙変換せず、一つの共通実装で四則演算します。</summary>
public sealed class ArithmeticNode : IRuntimeValueNode
{
    private readonly string operation;

    /// <summary>一つの演算を固定します。</summary>
    public ArithmeticNode(string operation)
    {
        this.operation = operation switch
        {
            "add" or "subtract" or "multiply" or "divide" => operation,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        Descriptor = NodeSupport.Describe("math." + operation, operation, "Math", """{"type":"object","properties":{}}""",
            inputs: [new PortDescriptor("left", EPortValueKind.Json, true), new PortDescriptor("right", EPortValueKind.Json, true)],
            outputs: [new PortDescriptor("value", EPortValueKind.Json)]) with { IsValueOperation = true, Description = "Calculate with finite IEEE 754 binary64 numbers. Strings require explicit conversion; division by zero and non-finite results fail." };
    }

    /// <summary>二つのJSON入力とJSON出力を宣言します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>設定を共通validatorで検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>有限な左右入力に演算を一回だけ適用します。</summary>
    /// <returns>数値または入力・ゼロ除算・範囲の失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        string leftPort = "left";
        string rightPort = "right";
        if (!context.Inputs.TryGetValue(leftPort, out NodeValue? leftInput) || !context.Inputs.TryGetValue(rightPort, out NodeValue? rightInput))
        {
            return ValueTask.FromResult(NodeResult.Failure("math.input.missing", "Both left and right inputs are required."));
        }
        try
        {
            double left = ScalarNumbers.Read(leftInput);
            double right = ScalarNumbers.Read(rightInput);
            string divide = "divide";
            if (string.Equals(operation, divide, StringComparison.Ordinal) && right == 0)
            {
                return ValueTask.FromResult(NodeResult.Failure("math.divide.zero", "Division by zero is not defined."));
            }
            double value = operation switch
            {
                "add" => left + right,
                "subtract" => left - right,
                "multiply" => left * right,
                "divide" => left / right,
                _ => throw new InvalidOperationException(),
            };
            bool underflow = operation switch
            {
                "multiply" or "divide" => value == 0 && left != 0 && right != 0,
                _ => false,
            };
            if (!double.IsFinite(value) || underflow)
            {
                return ValueTask.FromResult(NodeResult.Failure("math.result.range", "The result is outside the finite binary64 range."));
            }
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(value)));
        }
        catch (FormatException error)
        {
            return ValueTask.FromResult(NodeResult.Failure("math.input." + error.Message, "Both inputs must be finite JSON numbers. Use data.convert for strings."));
        }
    }
}
