namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>同じ絞込み条件を保持してスナップショット候補の続きを読みます。</summary>
/// <param name="Limit">最大件数。</param>
/// <param name="Cursor">前ページで受信した固定長cursor。</param>
/// <param name="MaximumBytes">応答JSONの最大バイト数。</param>
/// <param name="PeerId">取得元Peerの完全一致指定。</param>
/// <param name="TargetId">対象識別子の完全一致指定。</param>
/// <param name="Kind">状態種類の指定。</param>
public sealed record SnapshotListQuery(int Limit = 50, string? Cursor = null, int MaximumBytes = 524288,
    string? PeerId = null, string? TargetId = null, ESnapshotKind? Kind = null);
