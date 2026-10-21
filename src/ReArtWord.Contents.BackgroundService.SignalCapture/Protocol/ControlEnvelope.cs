namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>制御応答が出力済みになるまで次の要求を進めないための完了通知。</summary>
internal sealed record ControlEnvelope(PipeEvent Message, TaskCompletionSource Completion);
