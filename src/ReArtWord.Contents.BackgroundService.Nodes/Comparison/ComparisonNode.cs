using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Comparison;

/// <summary>大小比較・一致・不一致を、分岐せず真偽値として返します。</summary>
public sealed class ComparisonNode : IRuntimeValueNode
{
    private readonly string operation;
    private static string CompareOperation => "compare";
    private static string NotEqualOperation => "notEqual";

    /// <summary>独立したカタログ項目として比較方法を固定します。</summary>
    public ComparisonNode(string operation)
    {
        this.operation = operation switch
        {
            "compare" or "equal" or "notEqual" => operation,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        string schema = """{"type":"object","properties":{}}""";
        string description = "Return a boolean using JSON structural equality or byte equality. Types are not converted; strings are case-sensitive and array order matters.";
        EPortValueKind inputKind = EPortValueKind.Any;
        if (string.Equals(operation, CompareOperation, StringComparison.Ordinal))
        {
            schema = """{"type":"object","properties":{"operation":{"type":"string","enum":["less","lessOrEqual","greater","greaterOrEqual"],"default":"less"}}}""";
            description = "Compare left and right JSON numbers using the selected relation. Decimal digits are not rounded; strings require explicit conversion. Return a boolean.";
            inputKind = EPortValueKind.Json;
        }
        string name = operation switch { "compare" => "Compare", "equal" => "Is Equal", _ => "Is Not Equal" };
        Descriptor = NodeSupport.Describe("math." + operation, name, "Math", schema,
            [new PortDescriptor("left", inputKind, true), new PortDescriptor("right", inputKind, true)],
            [new PortDescriptor("value", EPortValueKind.Json)]) with { IsValueOperation = true, Description = description };
    }

    /// <summary>左右入力と真偽値出力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>選択肢を共通validatorで検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>入力不備をfalseへ置換せず、一回の判定を返します。</summary>
    /// <returns>真偽値または比較不能の失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeValue? left = NodeSupport.Input(context, "left");
        NodeValue? right = NodeSupport.Input(context, "right");
        if (left is null || right is null)
        {
            return ValueTask.FromResult(NodeResult.Failure("math.input.missing", "Both left and right inputs are required."));
        }
        try
        {
            bool result;
            if (string.Equals(operation, CompareOperation, StringComparison.Ordinal))
            {
                int order = ValueComparison.Order(left, right);
                result = new NodeSettings(context.Settings).ReadText("operation", "less") switch
                {
                    "less" => order < 0,
                    "lessOrEqual" => order <= 0,
                    "greater" => 0 < order,
                    "greaterOrEqual" => 0 <= order,
                    _ => throw new InvalidOperationException(),
                };
            }
            else
            {
                result = ValueComparison.Equal(left, right);
                if (string.Equals(operation, NotEqualOperation, StringComparison.Ordinal))
                {
                    result = !result;
                }
            }
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(result)));
        }
        catch (FormatException)
        {
            return ValueTask.FromResult(NodeResult.Failure("math.compare.input", "Comparison requires JSON numbers with at most 4096 characters and written and normalized decimal exponents in the Int32 range."));
        }
    }
}
