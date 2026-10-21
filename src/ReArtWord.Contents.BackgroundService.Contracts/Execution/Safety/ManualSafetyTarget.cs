namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>一時操作と通常判断を区別した一ノードの状態です。</summary>
/// <param name="NodeId">実際の対象ノード。</param>
/// <param name="NodeType">登録済み処理種類。</param>
/// <param name="Stopped">現在の実効停止。</param>
/// <param name="AutomaticallyStopped">一時操作を除いた通常判断。</param>
/// <param name="ManuallyStopped">ノードまたはGroupの人為停止。</param>
/// <param name="Continued">ノードまたはGroupの一時続行指定。</param>
/// <param name="StopReason">現在の停止理由です。</param>
/// <param name="AutomaticStopReason">人為操作を除いた通常判断の理由です。</param>
public sealed record ManualSafetyTarget(string NodeId, string NodeType, bool Stopped, bool AutomaticallyStopped, bool ManuallyStopped, bool Continued, string? StopReason = null, string? AutomaticStopReason = null);
