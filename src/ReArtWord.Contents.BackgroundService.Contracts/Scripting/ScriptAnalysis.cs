namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>コードと解析結果の取り違えを防ぐ指紋付きの診断です。</summary>
/// <param name="Fingerprint">言語・コード・明示型を含む検査対象の指紋です。</param>
/// <param name="Contract">検証済み入出力、または未確定です。</param>
/// <param name="Diagnostics">構文・未対応型・要選択箇所の説明です。</param>
/// <param name="SuggestedContract">未確定項目を画面から選択するための候補です。</param>
/// <param name="PackageEnvironmentId">解析と実行で固定するPeer上のパッケージ構成です。未指定は従来の標準環境です。</param>
/// <param name="ValueProtocolVersion">C#のバイナリ入出力を区別する解析版。0は旧Base64文字列契約です。</param>
public sealed record ScriptAnalysis(string Fingerprint, ScriptContract? Contract, IReadOnlyList<string> Diagnostics, ScriptContract? SuggestedContract = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? PackageEnvironmentId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int ValueProtocolVersion = 0);
