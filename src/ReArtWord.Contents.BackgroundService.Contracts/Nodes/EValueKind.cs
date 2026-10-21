namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>ワークフローを流れる値の表現です。</summary>
public enum EValueKind
{
    /// <summary>構造・数値を保持するJSON値です。</summary>
    Json,

    /// <summary>加工していないバイト列です。</summary>
    Binary,
}

