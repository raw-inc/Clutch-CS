using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;

/// <summary>有限なJSON数値の累乗と整数次のルートを計算します。</summary>
public sealed class PowerNode : IRuntimeValueNode
{
    private readonly string operation;

    /// <summary>累乗またはルートの演算と入力を固定します。</summary>
    public PowerNode(string operation)
    {
        this.operation = operation switch
        {
            "power" or "root" => operation,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        PortDescriptor[] inputs = operation switch
        {
            "power" => [new("base", EPortValueKind.Json, true), new("exponent", EPortValueKind.Json, true)],
            _ => [new("value", EPortValueKind.Json, true), new("degree", EPortValueKind.Json)],
        };
        Descriptor = NodeSupport.Describe("math." + operation, operation, "Math", """{"type":"object","properties":{}}""",
            inputs: inputs, outputs: [new PortDescriptor("value", EPortValueKind.Json)]) with
        {
            IsValueOperation = true,
            Description = operation switch
            {
                "power" => "Raise a finite binary64 base to a finite exponent. Zero to a negative exponent, non-real results, overflow and underflow fail; zero to zero is one.",
                _ => "Calculate a finite binary64 root. Degree defaults to two and must be an integer from one to 2147483647; negative values require an odd degree.",
            },
        };
    }

    /// <summary>JSON数値の入力と出力を宣言します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>設定を共通validatorで検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>渡された数値へ演算を一回適用します。</summary>
    /// <returns>有限数値または入力・定義域・範囲の失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        try
        {
            NodeResult result = operation switch
            {
                "power" => Power(context),
                _ => Root(context),
            };
            return ValueTask.FromResult(result);
        }
        catch (FormatException error)
        {
            return ValueTask.FromResult(NodeResult.Failure("math.input." + error.Message, "Inputs must be finite JSON numbers. Use data.convert for strings."));
        }
    }

    /// <summary>累乗の実数定義域を確認して標準演算を適用します。</summary>
    /// <returns>累乗値または未入力・定義域・範囲の失敗です。</returns>
    private static NodeResult Power(NodeExecutionContext context)
    {
        string basePort = "base";
        string exponentPort = "exponent";
        if (!context.Inputs.TryGetValue(basePort, out NodeValue? baseInput) || !context.Inputs.TryGetValue(exponentPort, out NodeValue? exponentInput))
        {
            return NodeResult.Failure("math.input.missing", "Both base and exponent inputs are required.");
        }
        double basis = ScalarNumbers.Read(baseInput);
        double exponent = ScalarNumbers.Read(exponentInput);
        if ((basis == 0 && exponent < 0) || (basis < 0 && exponent != Math.Truncate(exponent)))
        {
            return NodeResult.Failure("math.power.domain", "Zero requires a nonnegative exponent; a negative base requires an integer exponent.");
        }
        return Result(Math.Pow(basis, exponent), basis);
    }

    /// <summary>次数の正確な整数性と負数の定義域を確認します。</summary>
    /// <returns>ルート値または未入力・次数・定義域・範囲の失敗です。</returns>
    private static NodeResult Root(NodeExecutionContext context)
    {
        string valuePort = "value";
        string degreePort = "degree";
        if (!context.Inputs.TryGetValue(valuePort, out NodeValue? valueInput))
        {
            return NodeResult.Failure("math.input.missing", "The value input is required.");
        }
        double value = ScalarNumbers.Read(valueInput);
        long degree = 2;
        if (context.Inputs.TryGetValue(degreePort, out NodeValue? degreeInput))
        {
            _ = ScalarNumbers.Read(degreeInput);
            try
            {
                degree = ScalarNumbers.Integer(ScalarNumbers.Token(degreeInput));
            }
            catch (FormatException)
            {
                return NodeResult.Failure("math.root.degree", "Degree must be an integer from one to 2147483647.");
            }
        }
        if (degree < 1 || int.MaxValue < degree)
        {
            return NodeResult.Failure("math.root.degree", "Degree must be an integer from one to 2147483647.");
        }
        if (value < 0 && degree % 2 == 0)
        {
            return NodeResult.Failure("math.root.domain", "A negative value requires an odd degree.");
        }
        return Result(double.RootN(value, (int)degree), value);
    }

    /// <summary>演算後の非有限値と非ゼロ入力のunderflowを拒否します。</summary>
    /// <returns>有限JSON数値または範囲の失敗です。</returns>
    private static NodeResult Result(double value, double operand)
    {
        if (!double.IsFinite(value) || (value == 0 && operand != 0))
        {
            return NodeResult.Failure("math.result.range", "The result is outside the finite binary64 range.");
        }
        return NodeResult.Success(NodeValue.FromObject(value));
    }
}
