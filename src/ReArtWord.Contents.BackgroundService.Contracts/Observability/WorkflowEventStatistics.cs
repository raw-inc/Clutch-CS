namespace ReArtWord.Contents.BackgroundService.Contracts.Observability;

/// <summary>現在の購読数と、配信できなかった通知の数を表示します。</summary>
/// <param name="Subscribers">このPeerの購読数。</param>
/// <param name="Published">受け付けた異なる事象の数。</param>
/// <param name="DroppedDeliveries">購読先または送信キューに入らなかった配信の数。</param>
/// <param name="OmittedValues">容量上限のため値を省略した事象の数。</param>
/// <param name="LastProblem">最後に確認した配信上の問題。</param>
public sealed record WorkflowEventStatistics(int Subscribers, long Published, long DroppedDeliveries, long OmittedValues, string? LastProblem);
