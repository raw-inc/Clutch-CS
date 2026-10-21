using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>JSON Schemaに対応する設定欄と実ネットワーク候補を提供します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class SchemaFields
{
    private static string MatchInterfaceId => "interfaceId";
    private static string MatchProperties => "properties";
    private static string MatchSettings => "/settings/";
    private static string MatchSettingsWhole => "/settings/whole";

    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    [Inject]
    private SettingChoiceProvider ChoiceProvider
    {
        get;
        set;
    } = null!;

    /// <summary>ノードカード内のコンパクトな入力欄として表示するかどうかです。</summary>
    [Parameter]
    public bool Inline
    {
        get;
        set;
    }

    /// <summary>設定を編集するノード識別子です。</summary>
    [Parameter]
    public string NodeId
    {
        get;
        set;
    } = string.Empty;

    /// <summary>入力欄と検証条件を定義するJSON Schemaです。</summary>
    [Parameter]
    public JsonElement Schema
    {
        get;
        set;
    }

    /// <summary>現在編集中のノード設定値です。</summary>
    [Parameter]
    public JsonElement Settings
    {
        get;
        set;
    }

    /// <summary>設定への接続条件を判断する入力ポート定義です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<ReArtWord.Contents.BackgroundService.Contracts.Nodes.PortDescriptor> Inputs
    {
        get;
        set;
    } = [];

    /// <summary>検証した設定値を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<JsonElement> SettingsChanged
    {
        get;
        set;
    }

    /// <summary>入力内容の検証不能状態を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<bool> InvalidChanged
    {
        get;
        set;
    }

    private SettingNetworkChoices network = new(null, [], "Select one execution peer to verify these choices.");
    private int choicesGeneration;

    /// <summary>PC選択ノードの一覧欄を、任意の配列設定とは区別します。</summary>
    /// <returns>PC一覧から編集する設定ならtrue。</returns>
    private bool IsPcList(string key)
    {
        string hosts = "hostIds";
        return string.Equals(key, hosts, StringComparison.Ordinal) && Model.Draft.Document.Nodes.Any(node => string.Equals(node.Id, NodeId, StringComparison.Ordinal)
            && string.Equals(node.Type, PcSelectionPorts.SelectNode, StringComparison.Ordinal));
    }

    /// <summary>設定入力が接続によって供給されるかを判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool Connected(string key)
    {
        Contracts.Workflow.NodeDefinition? node = Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, NodeId, StringComparison.Ordinal));
        return node?.Inputs?.ContainsKey(SettingPorts.Name(key)) == true || Inputs.Any(port => string.Equals(port.OverridesSetting, key, StringComparison.Ordinal) && node?.Inputs?.ContainsKey(port.Name) == true) || Model.Draft.Document.Wires.Any(wire => string.Equals(wire.ToNode, NodeId, StringComparison.Ordinal) && (string.Equals(wire.ToPort, SettingPorts.Name(key), StringComparison.Ordinal) || Inputs.Any(port => string.Equals(port.Name, wire.ToPort, StringComparison.Ordinal) && string.Equals(port.OverridesSetting, key, StringComparison.Ordinal))));
    }

    /// <summary>必須値の未指定を判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private static bool Missing(string raw, JsonElement values) => SettingChoiceValues.Missing(raw, values);

    /// <summary>親から渡された対象に合わせて関連する状態を再確認します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnParametersSetAsync() => await ReadNetworkAsync(false).ConfigureAwait(true);

    /// <summary>設定候補に使う実ネットワーク情報の再取得を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshNetworkAsync() => await ReadNetworkAsync(true).ConfigureAwait(true);

    /// <summary>実行先に対応するネットワーク候補を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadNetworkAsync(bool refresh)
    {
        if (!Properties().Any(property => SettingChoiceProvider.Supports(property.Name) && Applicable(property.Value)))
        {
            return;
        }

        Contracts.Workflow.NodeDefinition? node = Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, NodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return;
        }

        int generation = ++choicesGeneration;
        SettingNetworkChoices? result = await ChoiceProvider.GetAsync(node, refresh, cancellationToken: UiCancellation).ConfigureAwait(true);
        if (generation == choicesGeneration)
        {
            network = result;
        }
    }

    /// <summary>ネットワーク候補を確認できない理由を表示します。</summary>
    /// <returns>取得失敗または接続由来の制約を示す翻訳キー、制約がなければnullです。</returns>
    private string? NetworkProblem(string key)
    {
        if (network.Problem is not null)
        {
            return network.Problem;
        }
        if (!string.Equals(key, MatchInterfaceId, StringComparison.Ordinal) && Connected(MatchInterfaceId))
        {
            return "The interface comes from a connection. Address choices are unverified.";
        }
        string id = Text(Current("interfaceId"));
        if (!Connected(MatchInterfaceId) && network.Interfaces.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal) && item.Unavailable))
        {
            return "The selected network adapter is not up. Requests cannot start until it is available; choose an active adapter and its IPv4.";
        }
        return null;
    }

    /// <summary>設定項目に対応するNICまたはアドレス候補を返します。</summary>
    /// <returns>設定値と表示名の組、確認済み候補がなければ空配列です。</returns>
    private KeyValuePair<string, string>[] NetworkOptions(string key)
    {
        if (network.Problem is not null)
        {
            return [];
        }

        if (string.Equals(key, MatchInterfaceId, StringComparison.Ordinal))
        {
            return network.Interfaces.Select(item => new KeyValuePair<string, string>(item.Id, item.DisplayName(Ui))).ToArray();
        }

        if (Connected(MatchInterfaceId))
        {
            return [];
        }

        string id = Text(Current("interfaceId"));
        return network.Interfaces.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))?.Addresses.Select(address => new KeyValuePair<string, string>(address, address)).ToArray() ?? [];
    }

    /// <summary>設定Schemaのプロパティを列挙します。</summary>
    /// <returns>入力欄に対応するプロパティの列挙子です。</returns>
    private IEnumerable<JsonProperty> Properties()
    {
        if (Schema.ValueKind != JsonValueKind.Object || !Schema.TryGetProperty(MatchProperties, out JsonElement properties))
        {
            return [];
        }
        return properties.EnumerateObject().Where(property => Inline || Applicable(property.Value));
    }

    /// <summary>条件を決める項目が接続されている場合は、入力欄を隠して断定しません。</summary>
    /// <returns>現在の選択で使用する項目かどうかです。</returns>
    private bool Applicable(JsonElement property)
    {
        string conditionKey = "x-visibleWhen";
        string propertyKey = "property";
        if (property.TryGetProperty(conditionKey, out JsonElement condition) && Connected(condition.GetProperty(propertyKey).GetString()!))
        {
            return true;
        }
        return NodeSettingVisibility.IsVisible(property, Settings);
    }

    /// <summary>選択条件が配線されている場合は保存値だけで選択肢を制限しません。</summary>
    /// <returns>現在の選択で使える候補です。</returns>
    private JsonElement ChoiceSchema(JsonElement property)
    {
        string conditionKey = "x-choicesWhen";
        string propertyKey = "property";
        if (property.TryGetProperty(conditionKey, out JsonElement condition) && Connected(condition.GetProperty(propertyKey).GetString()!))
        {
            return property;
        }
        return NodeSettingVisibility.Choices(property, Settings);
    }

    /// <summary>設定Schemaで必須指定された項目かを判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool Required(string key)
    {
        string requiredKey = "required";
        if (Schema.TryGetProperty(requiredKey, out JsonElement required) && required.EnumerateArray().Any(item => string.Equals(item.GetString(), key, StringComparison.Ordinal)))
        {
            return true;
        }
        return Properties().Any(property => string.Equals(property.Name, key, StringComparison.Ordinal) && NodeSettingVisibility.IsRequired(property.Value, Settings));
    }

    /// <summary>設定Schemaから入力欄に使用する型名を取得します。</summary>
    /// <returns>null以外の入力型、未指定なら既定の型名です。</returns>
    private static string TypeOf(JsonElement schema) => SettingChoiceValues.EditorType(schema);

    /// <summary>設定オブジェクトから対象項目の現在値を取得します。</summary>
    /// <returns>対象のJSON値、項目がなければUndefinedです。</returns>
    private JsonElement Current(string key) => Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(key, out JsonElement value) ? value
                                                                                                                                                 : default;

    /// <summary>表示対象の値を文字列へ変換します。</summary>
    /// <returns>JSON値の入力用文字列、未定義の場合は空文字列です。</returns>
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? string.Empty
                                                                                                : value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty
                                                                                                                                          : value.GetRawText();

    /// <summary>Schemaに指定された入力制約を取得します。</summary>
    /// <returns>制約値のJSON文字列、制約がなければnullです。</returns>
    private static string? Bound(JsonElement schema, string name) => schema.TryGetProperty(name, out JsonElement value) ? value.GetRawText()
                                                                                                                        : null;

    /// <summary>入力値を検証して編集対象へ反映し、検証状態を通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task CommitAsync(string key, string raw, string type, JsonElement choices = default)
    {
        if (Model.Draft.Document.Nodes.FirstOrDefault(node => string.Equals(node.Id, NodeId, StringComparison.Ordinal)) is not { } current
            || Model.Locks.Node(Model.Draft.Document.Id, NodeId) || Connected(key) || string.Equals(key, ResourceSettingContext.SourceHost, StringComparison.Ordinal) && Connected(ResourceSettingContext.SourcePeer))
        {
            return;
        }

        try
        {
            JsonObject values = ResourceSettingContext.EditSettings(current.Settings, key, raw);
            if (raw.Length == 0)
            {
                values.Remove(key);
            }
            else
            {
                if (choices.ValueKind == JsonValueKind.Array)
                {
                    if (!SettingChoiceValues.TryEnum(choices, raw, out JsonElement selected))
                    {
                        return;
                    }

                    values[key] = JsonNode.Parse(selected.GetRawText());
                }
                else
                {
                    values[key] = SettingInputValues.Parse(raw, type);
                }
            }

            if (Model.ReadInput(NodeId + MatchSettings + key) is not null)
            {
                Model.SetInput(NodeId + "/settings/" + key, null);
            }

            await SettingsChanged.InvokeAsync(JsonSerializer.SerializeToElement(values, ContractJson.Options)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            Model.SetInput(NodeId + "/settings/" + key, raw);
        }

        await InvalidChanged.InvokeAsync(Model.HasInvalidInputs).ConfigureAwait(true);
    }

    /// <summary>JSON全体を検証して設定の編集結果を通知します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task CommitWholeAsync(ChangeEventArgs args)
    {
        if (Model.Locks.Node(Model.Draft.Document.Id, NodeId))
        {
            return;
        }

        string raw = args.Value?.ToString() ?? string.Empty;
        try
        {
            using JsonDocument? doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(Ui["Settings must be an object."]);
            }

            if (Model.ReadInput(NodeId + MatchSettingsWhole) is not null)
            {
                Model.SetInput(NodeId + "/settings/whole", null);
            }

            await SettingsChanged.InvokeAsync(doc.RootElement.Clone()).ConfigureAwait(true);
            await InvalidChanged.InvokeAsync(Model.HasInvalidInputs).ConfigureAwait(true);
        }
        catch (JsonException)
        {
            Model.SetInput(NodeId + "/settings/whole", raw);
            await InvalidChanged.InvokeAsync(true).ConfigureAwait(true);
        }
    }
}
