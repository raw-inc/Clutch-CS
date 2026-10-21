namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>ポートが受け取れる最小の値区分です。</summary>
public enum EPortValueKind
{
    /// <summary>JSON値とバイト列を受け取れます。</summary>
    Any,

    /// <summary>JSON値を受け取れます。</summary>
    Json,

    /// <summary>バイト列を受け取れます。</summary>
    Binary,

    /// <summary>実行PCの選択条件です。通常のデータや設定値には接続できません。</summary>
    PcSelection,
}
