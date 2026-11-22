namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>操作開始時に確定し、保存と回復を通じて維持する派生IDの生成方式です。</summary>
public enum EDerivedIdFormat
{
    /// <summary>旧操作のIDを保持する、形式ビットを変更しないハッシュ先頭16バイトです。</summary>
    Legacy,

    /// <summary>時刻付きIDと区別できる、SHA-256由来のUUIDv8です。</summary>
    Uuid8,
}
