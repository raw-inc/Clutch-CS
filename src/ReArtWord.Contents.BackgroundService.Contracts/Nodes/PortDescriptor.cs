namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>入力・出力ポートの値と必須性を宣言します。</summary>
/// <param name="Name">ポート名。</param>
/// <param name="ValueKind">利用できる値区分。</param>
/// <param name="Required">入力の供給が必須か。</param>
/// <param name="OverridesSetting">この入力が渡されたときに代替する設定欄。表示時の読み取り専用判定に使います。</param>
public sealed record PortDescriptor(
    string Name,
    EPortValueKind ValueKind = EPortValueKind.Any,
    bool Required = false,
    string? OverridesSetting = null)
{
    /// <summary>クラスタ内レシピなどが公開する具体的な値の型です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Values.ValueTypeDefinition? DataType
    {
        get; init;
    }

    /// <summary>既存の接続を維持するためだけのポート。新規作成時は表示しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLegacy
    {
        get; init;
    }
}
