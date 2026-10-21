namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>一つの不変ログファイルと保持・転送状態です。</summary>
/// <param name="Id">ファイル識別子。</param>
/// <param name="Level">共通レベル。</param>
/// <param name="Date">固定したローリング日付。</param>
/// <param name="EntryIds">重複受信を拒否する識別子。</param>
/// <param name="Bytes">非圧縮のバイト数。</param>
/// <param name="Compressed">圧縮済みか。</param>
/// <param name="LocalOrigin">自Peerが生成し転送対象となるか。</param>
/// <param name="ForwardedTo">確認済みの転送先。</param>
/// <param name="ProtectTotalDays">既存短縮を保留したか。</param>
/// <param name="MinimumTotalDays">保護した総日数。nullは無期限。</param>
/// <param name="ExpandOnRecovery">明示再展開の未完了指示。</param>
/// <param name="FirstTime">ファイル内の最小時刻。旧索引では未確認。</param>
/// <param name="LastTime">ファイル内の最大時刻。追記順ではなく実際のログ時刻。</param>
internal sealed record LogSegment(Guid Id, string Level, DateOnly Date, IReadOnlyList<Guid> EntryIds, long Bytes, bool Compressed, bool LocalOrigin, IReadOnlyList<string> ForwardedTo,
    bool ProtectTotalDays = false, int? MinimumTotalDays = null, bool ExpandOnRecovery = false, DateTimeOffset? FirstTime = null, DateTimeOffset? LastTime = null);
