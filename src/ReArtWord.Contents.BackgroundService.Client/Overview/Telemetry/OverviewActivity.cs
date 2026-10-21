namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>取得した完了結果の実行IDを重複除外した活動量です。</summary>
internal sealed record OverviewActivity(string WorkflowId, int Completed, int Unsuccessful);
