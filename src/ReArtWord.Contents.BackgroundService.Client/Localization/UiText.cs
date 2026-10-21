using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Localization;

/// <summary>このブラウザーの表示言語と、原データを変更しないUI翻訳を提供します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントの注入プロパティ型です。
[SuppressMessage("Design", "CA1515", Justification = "公開Razor componentの共通基底から参照する表示型です。")]
public sealed class UiText
{
    private static readonly string[] languages = ["en", "ja"];
    private static readonly Dictionary<string, string> english = ReadResources(".en.json");
    private static readonly Dictionary<string, string> japanese = ReadResources(".ja.json");
    private readonly BrowserStorage storage;
    private bool initialized;

    /// <summary>既存のブラウザー保存境界を使用します。</summary>
    internal UiText(BrowserStorage storage)
    {
        this.storage = storage;
    }

    /// <summary>言語変更をレイアウトへ通知します。</summary>
    internal event Action? Changed;

    /// <summary>未指定時は英語を使用します。</summary>
    public string Language
    {
        get; private set;
    } = "en";

    /// <summary>表示設定自体を保持できなかった場合の理由です。</summary>
    public string? PreferenceProblem
    {
        get; private set;
    }

    /// <summary>UI語句を表示言語で取得します。記録本文の翻訳には使いません。</summary>
    public string this[string key] => Current.GetValueOrDefault(key, key);

    private Dictionary<string, string> Current
    {
        get
        {
            if (string.Equals(Language, languages[1], StringComparison.Ordinal))
            {
                return japanese;
            }
            return english;
        }
    }

    /// <summary>翻訳した書式へ表示値を埋め込みます。</summary>
    /// <returns>表示言語の文字列です。</returns>
    public string Format(string key, params object?[] values) => string.Format(CultureInfo.GetCultureInfo(Language), this[key], values.Select(FormatValue).ToArray());

    /// <summary>列挙状態だけを表示語へ変換し、原文や識別子を変更しません。</summary>
    /// <returns>翻訳された状態か元の値です。</returns>
    private object? FormatValue(object? value)
    {
        if (value is Enum state)
        {
            return this[state.ToString()];
        }
        return value;
    }

    /// <summary>取得時の値を保持し、描画時の表示言語で文を組み立てます。</summary>
    /// <returns>言語切替にも追従する表示です。</returns>
    public Func<string> Deferred(string key, params object?[] values) => () => Format(key, values);

    /// <summary>型付きの画面エラーだけを翻訳し、生の外部理由は保持します。</summary>
    /// <returns>例外の値を変えない表示用関数です。</returns>
    public Func<string> DeferredException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        UiError? error = UiError.From(exception);
        if (error is not null)
        {
            return Deferred(error.Template, error.Arguments.ToArray());
        }
        return Deferred("{0}", exception.Message);
    }

    /// <summary>標準ノードの表示名を契約識別子から取得します。</summary>
    /// <returns>翻訳済み表示名、拡張ノードでは提供された名前です。</returns>
    public string NodeName(NodeDescriptor? descriptor) => NodeText(descriptor, ".name", descriptor?.DisplayName);

    /// <summary>配置に論理名がある場合はその原文を優先し、未設定なら種類名を表示します。</summary>
    /// <returns>識別子とは独立したノードの表示名です。</returns>
    public string NodeName(Contracts.Workflow.NodeDefinition node, NodeDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.IsNullOrWhiteSpace(node.LogicalName))
        {
            return node.LogicalName;
        }
        return descriptor is null ? node.Type
                                  : NodeName(descriptor);
    }

    /// <summary>標準ノードの意味を表示言語で説明します。</summary>
    /// <returns>翻訳済み説明、拡張ノードでは提供された説明です。</returns>
    public string NodeDescription(NodeDescriptor? descriptor) => NodeText(descriptor, ".description", descriptor?.Description);

    /// <summary>設定キーを変更せず、登録済みの表示ラベルを取得します。</summary>
    /// <returns>表示名、または拡張ノードが定めた設定キーです。</returns>
    public string SettingName(string key) => Current.GetValueOrDefault("setting." + key, key);

    /// <summary>値の型を選ぶ欄にはポートと同じ型名を使い、保存する選択値は変更しません。</summary>
    /// <returns>組込みノードの型名、または通常の選択肢の表示名です。</returns>
    public string SettingChoiceName(string? nodeType, string key, string value)
    {
        bool typeChoice = (nodeType, key) switch
        {
            ("value.constant" or "value.type", "type") or ("data.convert", "targetType") => true,
            _ => false,
        };
        if (typeChoice && ReArtWord.Contents.BackgroundService.Contracts.Values.ValueTypeInference.KnownName(value))
        {
            return this["port.type." + value];
        }
        string specific = "choice." + nodeType + "." + key + "." + value;
        string shared = "choice." + key + "." + value;
        if (Current.TryGetValue(specific, out string? label) || Current.TryGetValue(shared, out label))
        {
            return label;
        }
        return this[value];
    }

    /// <summary>保存する識別子を変えず、ノードと入出力の役割に応じたポート名を表示します。</summary>
    /// <returns>登録済みの表示名、または拡張ノードが指定した元の名前です。</returns>
    public string PortName(string? nodeType, string port, bool outbound)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (nodeType is not null && IterationNodeTypes.IsEnd(nodeType) && ValuePortPairs.Index(port) is > 1 and var resultIndex)
        {
            string format = "Item result {0}";
            if (outbound)
            {
                format = "Collected results {0}";
            }
            return Format(format, resultIndex);
        }
        if (string.Equals(nodeType, ButtonValuePorts.NodeType, StringComparison.Ordinal) && ButtonValuePorts.Index(port) is > 0 and var valueIndex)
        {
            return Format(outbound ? "Pressed value {0}"
                                   : "Value on press {0}", valueIndex);
        }
        if (!outbound && nodeType is not null && Contracts.Scripting.ScriptSettings.IsScript(nodeType) && Contracts.Scripting.ScriptInputPorts.Argument(port) is { } scriptArgument)
        {
            return scriptArgument;
        }
        if (!outbound && string.Equals(nodeType, OscPorts.PacketNodeType, StringComparison.Ordinal) && ConcatPorts.Index(port) is > 0 and var packetIndex)
        {
            return Format("Argument {0}", packetIndex);
        }
        if (!outbound && (string.Equals(nodeType, ConcatPorts.NodeType, StringComparison.Ordinal) || nodeType is not null && EventMergePorts.IsNode(nodeType)) && ConcatPorts.Index(port) is > 0 and var inputIndex)
        {
            return Format("Input {0}", inputIndex);
        }
        string argumentPrefix = "argument";
        if (nodeType is not null && OscPorts.Supports(nodeType) && port.StartsWith(argumentPrefix, StringComparison.Ordinal)
            && int.TryParse(port.AsSpan(argumentPrefix.Length), out int argument) && 0 < argument)
        {
            return Format("Argument {0}", argument);
        }
        if (SettingPorts.Property(port) is { } setting)
        {
            return SettingName(setting);
        }
        string direction = "in";
        if (outbound)
        {
            direction = "out";
        }
        string key = "port." + nodeType + "." + direction + "." + port;
        if (Current.TryGetValue(key, out string? name))
        {
            return name;
        }
        bool common = port is "$execution.pc" or "trigger" || outbound && port is "result" or "fileTransaction" or "start" or "$completed" or "$succeeded";
        string nodeNameKey = "node." + nodeType + ".name";
        if (common || Current.ContainsKey(nodeNameKey))
        {
            return Current.GetValueOrDefault("port.common." + direction + "." + port, port);
        }
        return port;
    }

    /// <summary>端末で明示選択した言語だけを復元します。</summary>
    /// <returns>表示設定の初期化です。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }
        initialized = true;
        try
        {
            string? saved = await storage.ReadAsync<string>("contents.ui.language", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (saved is not null && languages.Contains(saved, StringComparer.Ordinal))
            {
                Language = saved;
            }
            await storage.SetLanguageAsync(Language, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or JsonException)
        {
            PreferenceProblem = "The saved language could not be read. English is being used.";
        }
    }

    /// <summary>ワークフローや接続先を変えず、この画面の言語を変更します。</summary>
    /// <returns>端末設定への保存確認です。</returns>
    public async Task ChangeAsync(string language, CancellationToken cancellationToken = default)
    {
        if (!languages.Contains(language, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }
        Language = language;
        PreferenceProblem = null;
        Changed?.Invoke();
        try
        {
            await storage.SetLanguageAsync(language, cancellationToken).ConfigureAwait(false);
            await storage.WriteAsync("contents.ui.language", language, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JSException)
        {
            PreferenceProblem = "The language was changed for this session, but could not be saved.";
            Changed?.Invoke();
        }
    }

    /// <summary>不明な拡張ノードの識別子を一般UI辞書と混ぜません。</summary>
    /// <returns>標準翻訳か拡張側の表示です。</returns>
    private string NodeText(NodeDescriptor? descriptor, string suffix, string? fallback)
    {
        string key = "node." + descriptor?.Type + suffix;
        return Current.GetValueOrDefault(key, fallback ?? string.Empty);
    }

    /// <summary>画面別の埋込語彙を一度だけ読み込みます。</summary>
    /// <returns>言語ごとの表示辞書です。</returns>
    private static Dictionary<string, string> ReadResources(string suffix)
    {
        Assembly assembly = typeof(UiText).Assembly;
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.Contains(".Localization.", StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name);
            Dictionary<string, string> entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            foreach ((string key, string value) in entries)
            {
                result.TryAdd(key, value);
            }
        }
        return result;
    }
}
