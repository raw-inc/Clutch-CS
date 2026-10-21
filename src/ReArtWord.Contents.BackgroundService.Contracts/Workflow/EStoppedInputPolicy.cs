namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>安全停止中に到着した入力の扱いです。</summary>
public enum EStoppedInputPolicy
{
    /// <summary>停止中の入力を破棄します。</summary>
    Drop,

    /// <summary>最新の一件だけを保持します。</summary>
    Latest,

    /// <summary>到着順に全件を保持します。</summary>
    All,
}

