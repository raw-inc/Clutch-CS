using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>ユーザーコードを実行せず、入出力契約を検査する要求です。</summary>
/// <param name="Language">csharp、python、javascriptです。</param>
/// <param name="Code">今回検査するコードです。</param>
/// <param name="ManualContract">動的言語で推定できない箇所の明示型です。</param>
/// <param name="PythonPath">Python解析に使用する実行先上の実行ファイルです。</param>
/// <param name="SyntaxOnly">コードブロックの構文だけを調べ、実行ノードの入出力契約を要求しません。</param>
/// <param name="NodePath">JavaScriptの解析に使用するNode.js実行ファイルです。</param>
/// <param name="Continuous">C#の非同期列挙を継続待受として解析します。</param>
public sealed record ScriptAnalysisRequest(string Language, string Code,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] JsonElement ManualContract = default, string? PythonPath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool SyntaxOnly = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NodePath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Continuous = false);
