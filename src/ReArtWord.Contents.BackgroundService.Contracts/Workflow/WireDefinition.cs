namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>一件の実行内で出力を一つの入力へ渡す接続です。</summary>
/// <param name="FromNode">出力元ノード。</param>
/// <param name="FromPort">出力ポート。</param>
/// <param name="ToNode">入力先ノード。</param>
/// <param name="ToPort">入力ポート。</param>
/// <param name="When">successは正常結果全般、その他は明示経路名です。</param>
/// <param name="Path">JSON Pointerによる抽出。未存在は正常nullになります。</param>
public sealed record WireDefinition(
    string FromNode,
    string FromPort,
    string ToNode,
    string ToPort,
    string When = "success",
    string? Path = null);

