namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>宛先が永続保存を確認した結果です。</summary>
/// <param name="PeerId">実際の宛先。</param>
/// <param name="Accepted">全件の永続受付を確認したか。</param>
internal sealed record LogTransferAck(string PeerId, bool Accepted);
