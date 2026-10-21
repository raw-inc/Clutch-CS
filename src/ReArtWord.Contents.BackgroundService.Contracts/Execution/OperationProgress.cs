namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>実処理で確認した工程数と転送量です。時間に基づく推定値は含みません。</summary>
/// <param name="Stage">現在の工程を示す安定した識別子です。</param>
/// <param name="CompletedSteps">終了を確認した工程数です。</param>
/// <param name="TotalSteps">開始時に固定した工程数です。未確定なら0です。</param>
/// <param name="Target">処理中の対象です。</param>
/// <param name="CompletedBytes">応答を確認した転送済みbyte数です。</param>
/// <param name="TotalBytes">現在の対象へ転送する総byte数です。</param>
public sealed record OperationProgress(string Stage, int CompletedSteps = 0, int TotalSteps = 0, string? Target = null, long CompletedBytes = 0, long TotalBytes = 0);
