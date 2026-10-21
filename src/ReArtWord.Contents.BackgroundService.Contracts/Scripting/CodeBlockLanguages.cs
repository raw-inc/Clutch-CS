namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting;

/// <summary>コードブロックの保存検証と画面選択で共通の言語識別子を使用します。</summary>
public static class CodeBlockLanguages
{
    /// <summary>既存の保存値を維持した、表示用の言語一覧です。</summary>
    public static IReadOnlyList<string> All
    {
        get;
    } = Array.AsReadOnly<string>(["text", "csharp", "python", "javascript", "bash", "powershell", "json", "toml", "yaml"]);

    /// <summary>色分けとは別に、既存のPeer側構文検査を利用できる言語かを確認します。</summary>
    /// <returns>構文検査を提供している場合だけtrueです。</returns>
    public static bool SupportsAnalysis(string language) => language is "csharp" or "python" or "javascript";
}
