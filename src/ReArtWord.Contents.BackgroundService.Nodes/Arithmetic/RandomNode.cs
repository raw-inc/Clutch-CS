using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;

/// <summary>指定範囲の整数または小数を、一回の実行につき一つ生成します。</summary>
public sealed class RandomNode : IRuntimeValueNode
{
    /// <summary>範囲と数値区分を設定または設定口から受け取ります。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("math.random", "Random number", "Math", """
        {"type":"object","properties":{
          "mode":{"type":"string","enum":["integer","decimal"],"default":"integer","x-runtime-input":true,"description":"Integers include both bounds. Decimals use finite binary64 values and exclude the upper bound unless both bounds are equal."},
          "minimum":{"type":"number","default":0,"x-runtime-input":true,"description":"Inclusive lower bound. Integer mode requires an exact signed 64-bit integer."},
          "maximum":{"type":"number","default":1,"x-runtime-input":true,"description":"Upper bound, at least the minimum. Integer mode includes this bound; decimal mode excludes it. Equal bounds return that value."}}}
        """, inputs: [], outputs: [new("value", EPortValueKind.Json)]) with { IsValueOperation = true };

    /// <summary>抽選せずに範囲と整数性を確認し、不正な範囲を自動交換しません。</summary>
    /// <returns>保存または実行前に訂正できる設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (issues.Count != 0)
        {
            return issues;
        }
        NodeSettings settings = new(definition.Settings);
        try
        {
            if (IsInteger(settings))
            {
                (long minimum, long maximum) = IntegerBounds(settings);
                if (maximum < minimum)
                {
                    throw new FormatException("range.order");
                }
            }
            else
            {
                (double minimum, double maximum) = DecimalBounds(settings);
                if (maximum < minimum)
                {
                    throw new FormatException("range.order");
                }
            }
        }
        catch (FormatException error)
        {
            issues.Add(new("random." + error.Message, "Specify finite numeric bounds with minimum <= maximum. Integer mode requires exact signed 64-bit integers.", definition.Id));
        }
        return issues;
    }

    /// <summary>検証した範囲内で一回抽選し、固定した結果を後続へ渡します。</summary>
    /// <returns>整数、小数、または不正な設定の結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeSettings settings = new(context.Settings);
        if (IsInteger(settings))
        {
            (long minimum, long maximum) = IntegerBounds(settings);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(Integer(minimum, maximum))));
        }
        (double lower, double upper) = DecimalBounds(settings);
        double value = lower;
        if (lower != upper)
        {
            double fraction = (RandomBits() >> 11) * (1.0 / (1UL << 53));
            double width = upper - lower;

            if (double.IsFinite(width))
            {
                value = lower + fraction * width;
            }
            else
            {
                value = (1 - fraction) * lower + fraction * upper;
            }

            // 丸めで上端へ到達しても、公開した上端未満の契約を保ちます。
            value = Math.Clamp(value, lower, Math.BitDecrement(upper));
        }
        return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(value)));
    }

    /// <summary>整数の両端を含み、Int64最大値と全域でも加算をオーバーフローさせません。</summary>
    /// <returns>指定範囲の一つの整数です。</returns>
    private static long Integer(long minimum, long maximum)
    {
        if (minimum == maximum)
        {
            return minimum;
        }
        ulong range = unchecked((ulong)maximum - (ulong)minimum);
        ulong mask = ulong.MaxValue >> System.Numerics.BitOperations.LeadingZeroCount(range);
        ulong selected;

        // 余り演算による偏りを避け、範囲外のビット列だけを引き直します。
        do
        {
            selected = RandomBits() & mask;
        }
        while (range < selected);
        return unchecked((long)((ulong)minimum + selected));
    }

    /// <summary>整数と小数で共通の64bit乱数源を使用します。</summary>
    /// <returns>全ビットが抽選対象の符号なし整数です。</returns>
    private static ulong RandomBits()
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    /// <summary>省略時は整数モードを使用します。</summary>
    /// <returns>整数の範囲として解釈するかどうかです。</returns>
    private static bool IsInteger(NodeSettings settings) => settings.ReadText("mode", "integer") is "integer";

    /// <summary>JSON数値の元の精度を維持して整数範囲を読みます。</summary>
    /// <returns>丸めていない上下限です。</returns>
    private static (long Minimum, long Maximum) IntegerBounds(NodeSettings settings)
        => (ScalarNumbers.Integer(Token(settings, "minimum", "0")), ScalarNumbers.Integer(Token(settings, "maximum", "1")));

    /// <summary>有限binary64数値として小数範囲を読みます。</summary>
    /// <returns>上下限です。</returns>
    private static (double Minimum, double Maximum) DecimalBounds(NodeSettings settings)
        => (ScalarNumbers.Parse(Token(settings, "minimum", "0")), ScalarNumbers.Parse(Token(settings, "maximum", "1")));

    /// <summary>未指定だけに既定値を適用し、指定数値の文字列表現を保ちます。</summary>
    /// <returns>数値トークンです。</returns>
    private static string Token(NodeSettings settings, string name, string fallback)
    {
        System.Text.Json.JsonElement value = settings.Get(name);
        if (value.ValueKind is System.Text.Json.JsonValueKind.Undefined)
        {
            return fallback;
        }
        return value.GetRawText();
    }
}
