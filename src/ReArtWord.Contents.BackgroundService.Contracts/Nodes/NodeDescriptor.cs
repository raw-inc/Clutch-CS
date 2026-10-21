using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>拡張ノードの選択・検証・設定画面に共通で使用する情報です。</summary>
/// <param name="Type">安定した種類識別子。</param>
/// <param name="DisplayName">表示名。</param>
/// <param name="Category">分類名。</param>
/// <param name="Description">役割の説明。</param>
/// <param name="Kind">有限処理または継続処理。</param>
/// <param name="Location">実行場所の指定条件。</param>
/// <param name="Inputs">入力ポート。</param>
/// <param name="Outputs">出力ポート。</param>
/// <param name="Routes">正常結果の経路名。</param>
/// <param name="SettingsSchema">設定のJSON Schema。</param>
/// <param name="IsValueOperation">値の生成・変換・演算を示す表示用分類です。静的評価の可否とは別です。</param>
/// <param name="CanEvaluateStatically">登録実装が外部処理なしで設定値を評価できるか。旧カタログでは未確認です。</param>
/// <param name="ContractVersion">実装が提供する現在の動作契約。</param>
/// <param name="CompatibleContractVersions">現在版に加え、同じ実装が期待される動作を保てる旧契約の版。</param>
/// <param name="ContractNotes">契約の変更内容や、利用者が確認する設定の説明。</param>
public sealed record NodeDescriptor(
    string Type,
    string DisplayName,
    string Category,
    string Description,
    ENodeKind Kind,
    EExecutionLocation Location,
    IReadOnlyList<PortDescriptor> Inputs,
    IReadOnlyList<PortDescriptor> Outputs,
    IReadOnlyList<string> Routes,
    JsonElement SettingsSchema,
    bool IsValueOperation = false,
    bool? CanEvaluateStatically = null,
    int ContractVersion = 1,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? CompatibleContractVersions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ContractNotes = null)
{
    /// <summary>既存保存定義や内部展開の解決には使い、新規配置レシピには表示しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsHidden
    {
        get; init;
    }

    /// <summary>外部I/O・待機・反復全体等の終了を待つ処理です。値計算や分岐、遠隔配送だけでは指定しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasAsyncCompletion
    {
        get; init;
    }

    /// <summary>共通resultを含め、後続への配線を持たない終端ノードです。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsTerminal
    {
        get; init;
    }
}
