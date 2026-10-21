namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>同じノードを同じPeerで実行中に次の一件が届いた場合の方針です。</summary>
public enum EOverlapPolicy
{
    /// <summary>前回と並行して開始します。</summary>
    Parallel,

    /// <summary>実行中は新しい一件を見送ります。</summary>
    Skip,

    /// <summary>保留を最新の一件へ置き換えます。</summary>
    Latest,

    /// <summary>容量の範囲で受付順に待機します。</summary>
    Queue,
}
