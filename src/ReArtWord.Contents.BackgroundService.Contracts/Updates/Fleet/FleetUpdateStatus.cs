using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>ClientHostが画面寿命から独立して保存する全体更新の進捗です。</summary>
/// <param name="Request">変更しない元要求です。</param>
/// <param name="Completed">この自動処理が終了したかです。成功とは別です。</param>
/// <param name="Outcome">確認済みの全体結果です。</param>
/// <param name="Peers">対象別進捗です。</param>
/// <param name="UpdatedAt">進捗の保存時刻です。</param>
/// <param name="Reason">全体の停止理由です。</param>
/// <param name="Client">ClientHost自身の更新状態です。未指定の従来配布ではnullです。</param>
public sealed record FleetUpdateStatus(FleetUpdateRequest Request, bool Completed, EOutcome Outcome,
    IReadOnlyList<FleetUpdatePeerProgress> Peers, DateTimeOffset UpdatedAt, string? Reason = null, ClientUpdateProgress? Client = null);
