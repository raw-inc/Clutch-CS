namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>確認済みの登録アプリケーションの起動・終了です。引数や実行ファイルの内容は含めません。</summary>
/// <param name="Id">登録識別子です。</param>
/// <param name="LogicalName">表示用の論理名です。</param>
/// <param name="Kind">started、stopped、restartedのいずれかです。</param>
/// <param name="InstanceId">事象が属する起動回です。</param>
/// <param name="Identity">確認したOS実体です。</param>
/// <param name="ExitCode">確認できた終了コードです。</param>
/// <param name="PlannedStop">明示した停止操作による終了かです。</param>
/// <param name="OccurredAt">観測した時刻です。</param>
public sealed record ProcessLifecycleEvent(string Id, string LogicalName, string Kind, Guid InstanceId, ProcessIdentity? Identity,
    int? ExitCode, bool PlannedStop, DateTimeOffset OccurredAt);
