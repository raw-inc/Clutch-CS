using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>WorkflowEditorと実行前確認で共有するスクリプトのJSON契約です。</summary>
/// <param name="Arguments">Runまたはrunの名前付き引数です。</param>
/// <param name="Result">返却値の型です。</param>
/// <param name="VariadicArgument">単一値または配列で受け取る可変長引数です。</param>
public sealed record ScriptContract(IReadOnlyDictionary<string, ValueTypeDefinition> Arguments, ValueTypeDefinition Result,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? VariadicArgument = null);
