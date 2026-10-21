namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>ログの保存・キュー・明示TCP接続設定です。</summary>
/// <param name="PeerId">PeerIdの指定値。</param>
/// <param name="Directory">Directoryの指定値。</param>
/// <param name="Retention">Retentionの指定値。</param>
/// <param name="QueueBytes">QueueBytesの指定値。</param>
/// <param name="InterfaceId">InterfaceIdの指定値。</param>
/// <param name="LocalAddress">LocalAddressの指定値。</param>
/// <param name="ListenPort">ListenPortの指定値。</param>
/// <param name="ForwardTargets">ForwardTargetsの指定値。</param>
/// <param name="RollingTime">全レベル共通の日付切替時刻HH:mm。</param>
/// <param name="TimeZoneId">全レベル共通のローリングtimezone。</param>
public sealed record LogOptions(string PeerId, string Directory, IReadOnlyList<LogRetentionRule>? Retention = null, int QueueBytes = 16777216, string? InterfaceId = null, string? LocalAddress = null, int? ListenPort = null, IReadOnlyList<LogForwardTarget>? ForwardTargets = null, string RollingTime = "00:00", string TimeZoneId = "UTC");
