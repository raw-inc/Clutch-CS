namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>必要な実行場所の指定条件です。</summary>
public enum EExecutionLocation
{
    /// <summary>未指定時にCoordinatorを使用できます。</summary>
    Independent,

    /// <summary>PC固有の環境を使用する処理です。実行PCは共通の選択規則で決めます。</summary>
    ExplicitPeer,
}
