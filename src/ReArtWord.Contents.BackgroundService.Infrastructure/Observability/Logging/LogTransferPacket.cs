using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>一接続で確認する有限ログ転送です。</summary>
/// <param name="SenderPeer">送出元。</param>
/// <param name="TargetPeer">接続先で照合する宛先。</param>
/// <param name="Entries">伏字済みの一一括処理。</param>
internal sealed record LogTransferPacket(string SenderPeer, string TargetPeer, IReadOnlyList<WorkflowLogEntry> Entries);
