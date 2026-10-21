namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>選択したPCのうち、何台で処理するかを指定します。</summary>
public enum EPcExecutionMode
{
    /// <summary>実行可能な候補のうち最優先のPCで一回実行します。</summary>
    One,

    /// <summary>選択した各PCで一回ずつ実行します。</summary>
    All,
}
