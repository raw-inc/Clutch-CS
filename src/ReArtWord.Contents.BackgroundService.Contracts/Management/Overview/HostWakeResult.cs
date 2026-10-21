using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>パケット送信の結果です。PCの起動確認とは区別します。</summary>
/// <param name="OperationId">元要求の識別子です。</param>
/// <param name="Outcome">全送信・一部失敗・未送信・確認不能です。</param>
/// <param name="PacketsSent">ソケットが送信を受け付けたパケット数です。</param>
/// <param name="InterfacesUsed">送信に使用できたIPv4の口の数です。</param>
/// <param name="Reason">表示する結果コードです。</param>
public sealed record HostWakeResult(Guid OperationId, EOutcome Outcome, int PacketsSent, int InterfacesUsed, string? Reason = null);
