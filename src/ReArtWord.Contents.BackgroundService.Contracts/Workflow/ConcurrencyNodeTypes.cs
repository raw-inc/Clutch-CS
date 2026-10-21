namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>同時実行数を制限する開始と、任意の終了境界の識別子です。</summary>
public static class ConcurrencyNodeTypes
{
    /// <summary>クラスタ内の同じワークフロー・ノードで枠を共有する開始境界です。</summary>
    public static string Start => "control.concurrencyLimit";

    /// <summary>監視区間の終了境界です。</summary>
    public static string End => "control.concurrencyLimit.end";

    /// <summary>開始と終了を一対一に対応付ける専用接続です。</summary>
    public static string ScopePort => "scope";

    /// <summary>制限開始を判定します。</summary>
    /// <returns>制限開始ノードならtrueです。</returns>
    public static bool IsStart(string type) => string.Equals(type, Start, StringComparison.Ordinal);

    /// <summary>制限終了を判定します。</summary>
    /// <returns>制限終了ノードならtrueです。</returns>
    public static bool IsEnd(string type) => string.Equals(type, End, StringComparison.Ordinal);
}
