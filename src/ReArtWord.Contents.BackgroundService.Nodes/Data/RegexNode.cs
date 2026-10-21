using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>C#準拠の正規表現から、キャプチャ群または一致有無を返します。</summary>
public sealed class RegexNode(bool predicate = false) : IRuntimeValueNode
{
    private readonly Lock patternGate = new();
    private readonly Dictionary<(string Pattern, bool IgnoreCase, bool Multiline, bool Singleline, int Timeout), RegexTextPattern> patterns = [];

    /// <summary>正規表現・対象テキストは配線でき、照合方針は通常の設定入力で指定できます。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = CreateDescriptor(predicate);

    /// <summary>固定パターンの構文エラーをノード検証へ反映します。</summary>
    /// <returns>設定または固定パターンの問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        string patternPort = "pattern";
        if (definition.Inputs?.GetValueOrDefault(patternPort) is { Kind: EValueKind.Json } pattern && pattern.Json.ValueKind == JsonValueKind.String)
        {
            try
            {
                _ = ResolvePattern(pattern.GetString()!);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                issues.Add(new("text.regex.pattern.invalid", "Invalid .NET regular expression or capture limit exceeded.", definition.Id, "pattern"));
            }
        }
        return issues;
    }

    /// <summary>一件ごとのRegex期限に加え、全一致列挙の時間と出力量も制限します。</summary>
    /// <returns>全て確認した結果、または途中結果を含まない明示的な失敗です。</returns>
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
            string text = Text(context, "value");
            string pattern = Text(context, "pattern");
            if (1048576 < text.Length)
            {
                return ValueTask.FromResult(NodeResult.Failure("text.regex.input.limit", "Text exceeds 1,048,576 UTF-16 units."));
            }
            NodeSettings settings = new(context.Settings);
            RegexTextPattern compiled = ResolvePattern(pattern, settings.ReadBoolean("ignoreCase"), settings.ReadBoolean("multiline"), settings.ReadBoolean("singleline"), settings.ReadInt32("timeoutMilliseconds", 1000));
            if (predicate)
            {
                bool matched = compiled.Expression.IsMatch(text);
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(matched)));
            }
            string first = "first";
            return ValueTask.FromResult(Capture(compiled, text, string.Equals(settings.ReadText("mode", "all"), first, StringComparison.Ordinal), cancellationToken));
        }
        catch (RegexMatchTimeoutException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.regex.timeout", "Regular expression matching exceeded its time limit."));
        }
        catch (FormatException exception)
        {
            return ValueTask.FromResult(NodeResult.Failure("text." + exception.Message, "Text, pattern or output limits are invalid."));
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.regex.pattern.invalid", "The pattern is not a valid .NET regular expression."));
        }
    }

    /// <summary>照合方針と期限も含めて検証済みのパターンを再利用し、動的入力も最大64種類へ制限します。</summary>
    /// <returns>照合ごとの可変状態を持たない、同じ条件のパターンです。</returns>
    private RegexTextPattern ResolvePattern(string pattern, bool ignoreCase = false, bool multiline = false, bool singleline = false, int timeout = 1000)
    {
        (string Pattern, bool IgnoreCase, bool Multiline, bool Singleline, int Timeout) key = (pattern, ignoreCase, multiline, singleline, timeout);
        using (patternGate.EnterScope())
        {
            if (patterns.TryGetValue(key, out RegexTextPattern? found))
            {
                return found;
            }
            RegexTextPattern compiled = new(pattern, ignoreCase, multiline, singleline, timeout);
            if (64 <= patterns.Count)
            {
                patterns.Remove(patterns.Keys.First());
            }
            patterns.Add(key, compiled);
            return compiled;
        }
    }

    /// <summary>Group.Valueの最後の一件に丸めず、全Captureを出現順に保持します。</summary>
    /// <returns>グループ名のObjectと一致件数です。</returns>
    private static NodeResult Capture(RegexTextPattern compiled, string text, bool firstOnly, CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<string>> captures = compiled.Names.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        Stopwatch elapsed = Stopwatch.StartNew();
        Match match = compiled.Expression.Match(text);
        int count = 0, captureCount = 0, characters = 0;
        while (match.Success)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compiled.Expression.MatchTimeout < elapsed.Elapsed)
            {
                return NodeResult.Failure("text.regex.timeout", "All-match enumeration exceeded its time limit.");
            }
            if (4096 <= count)
            {
                throw new FormatException("regex.matches.limit");
            }
            count++;
            foreach (string name in compiled.Names)
            {
                foreach (Capture capture in match.Groups[name].Captures)
                {
                    characters = checked(characters + capture.Length);
                    if (16384 <= captureCount || 4194304 < characters)
                    {
                        throw new FormatException("regex.output.limit");
                    }
                    captureCount++;
                    TextValues.ValidateUnicode(capture.Value);
                    captures[name].Add(capture.Value);
                }
            }
            if (firstOnly)
            {
                break;
            }
            match = match.NextMatch();
        }
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        if (compiled.Expression.MatchTimeout < elapsed.Elapsed)
        {
            return NodeResult.Failure("text.regex.timeout", "All-match enumeration exceeded its time limit.");
        }
        foreach (string name in compiled.Names)
        {
            values.Add(name, compiled.IsArray(name, firstOnly) ? captures[name].ToArray()
                                                               : captures[name].SingleOrDefault());
        }
        return new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(values),
            ["count"] = NodeValue.FromObject(count),
        });
    }

    /// <summary>値の暗黙文字列化を避け、テキスト用の入力だけを受け取ります。</summary>
    /// <returns>入力された文字列です。</returns>
    private static string Text(NodeExecutionContext context, string name)
    {
        NodeValue? value = context.Inputs.GetValueOrDefault(name);
        if (value?.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("regex.text.required");
        }
        return value.GetString()!;
    }

    /// <summary>一致判定と抽出の設定を共有し、抽出だけに取得モードと件数を追加します。</summary>
    /// <returns>既存カテゴリと通常の配線に対応する定義です。</returns>
    private static NodeDescriptor CreateDescriptor(bool predicate)
    {
        string type = "text.regex", name = "Regex";
        if (predicate)
        {
            type = "text.match";
            name = "Match";
        }
        System.Text.Json.Nodes.JsonObject schema = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{"ignoreCase":{"type":"boolean","default":false},"multiline":{"type":"boolean","default":false},"singleline":{"type":"boolean","default":false},"timeoutMilliseconds":{"type":"integer","minimum":1,"maximum":10000,"default":1000}}}""")!.AsObject();
        List<PortDescriptor> outputs = [new("value", EPortValueKind.Json)];
        if (!predicate)
        {
            schema["properties"]!["mode"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["all","first"],"default":"all"}""");
            outputs.Add(new("count", EPortValueKind.Json));
        }
        return NodeSupport.Describe(type, name, "Data", schema.ToJsonString(),
            inputs: [new("value", EPortValueKind.Json, true), new("pattern", EPortValueKind.Json, true)], outputs: outputs)
            with { IsValueOperation = true, Description = "Use .NET regular expressions. Regex returns captures by name; Match returns whether any match exists. Invalid patterns and timeouts fail." };
    }
}
