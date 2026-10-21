namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>明示した対象集合内での実行回数です。</summary>
public enum EExecutionScope
{
    /// <summary>指定した一つのPeerで実行します。</summary>
    One,

    /// <summary>指定集合の適格な一つで実行します。</summary>
    Any,

    /// <summary>指定した各Peerで一回ずつ実行します。</summary>
    All,
}

