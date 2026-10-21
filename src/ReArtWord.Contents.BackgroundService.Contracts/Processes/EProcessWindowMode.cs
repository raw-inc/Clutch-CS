namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>起動時のウィンドウ表示指定です。</summary>
public enum EProcessWindowMode
{
    /// <summary>アプリケーションの既定表示を使用します。</summary>
    Default,

    /// <summary>コンソールを作成せず、初期ウィンドウも非表示にするよう要求します。</summary>
    NoWindow,
}
