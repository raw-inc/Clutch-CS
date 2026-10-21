using System.Text.Json;
using System.Text.RegularExpressions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>.NETの正規表現と、編集・実行で共通の名前付き出力契約を構成します。</summary>
public sealed class RegexTextPattern
{
    private readonly HashSet<string> repeated;

    /// <summary>実エンジンで構文と名前を確認し、照合には必ず有限の期限を指定します。</summary>
    public RegexTextPattern(string pattern, bool ignoreCase = false, bool multiline = false, bool singleline = false, int timeoutMilliseconds = 1000)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (8192 < pattern.Length || timeoutMilliseconds is < 1 or > 10000)
        {
            throw new FormatException("regex.limits");
        }
        RegexOptions options = RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }
        if (multiline)
        {
            options |= RegexOptions.Multiline;
        }
        if (singleline)
        {
            options |= RegexOptions.Singleline;
        }
        Expression = new(pattern, options, TimeSpan.FromMilliseconds(timeoutMilliseconds));
        Names = Expression.GetGroupNames();
        if (128 < Names.Count)
        {
            throw new FormatException("regex.groups.limit");
        }
        repeated = RegexCaptureShape.Repeated(Expression, pattern);
    }

    /// <summary>名前と照合意味を決定するC#標準のエンジンです。</summary>
    public Regex Expression
    {
        get;
    }

    /// <summary>0は一致全体、他は.NETが割り当てた番号またはグループ名です。</summary>
    public IReadOnlyList<string> Names
    {
        get;
    }

    /// <summary>最初のみでも一グループに複数キャプチャがある場合は配列を保持します。</summary>
    /// <returns>配列として取り出す出力かです。</returns>
    public bool IsArray(string name, bool firstOnly) => !firstOnly || repeated.Contains(name);

    /// <summary>実際の一致数で型を変えず、不一致も受け取れる出力を宣言します。</summary>
    /// <returns>個別グループ口へ展開するObject型です。</returns>
    public ValueTypeDefinition OutputType(bool? firstOnly)
    {
        Dictionary<string, ValueTypeDefinition> fields = new(StringComparer.Ordinal);
        foreach (string name in Names)
        {
            ValueTypeDefinition type = new("any");
            if (firstOnly.HasValue)
            {
                type = IsArray(name, firstOnly.Value) ? new("array", new("string"))
                                                      : new("string", Optional: true);
            }
            fields.Add(name, type);
        }
        return new("object", Properties: fields);
    }

    /// <summary>固定パターンの名前を公開し、取得モードが動的な場合は出力型を断定しません。</summary>
    /// <returns>グループ名と単一・配列の出力型です。</returns>
    internal static ValueTypeDefinition Infer(NodeDefinition node, WorkflowSettingValues settings)
    {
        string modeKey = "mode", first = "first", patternPort = "pattern";
        if (settings.Input(node, patternPort) is not { Kind: EValueKind.Json } pattern || pattern.Json.ValueKind != JsonValueKind.String)
        {
            return new("object");
        }
        try
        {
            NodeValue? mode = settings.Setting(node, modeKey);
            bool? firstOnly = string.Equals(mode?.GetString(), first, StringComparison.Ordinal);
            if (mode is null && settings.HasInput(node, SettingPorts.Name(modeKey)))
            {
                firstOnly = null;
            }
            return new RegexTextPattern(pattern.GetString()!).OutputType(firstOnly);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return new("object");
        }
    }

}
