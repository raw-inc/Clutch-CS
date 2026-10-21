namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>汎用ワークフローが現起動回について判定した状態を渡します。</summary>
/// <param name="InstanceId">古い応答を現プロセスへ誤適用しない起動回です。</param>
/// <param name="Ready">準備状態。nullは判定不能です。</param>
/// <param name="Healthy">利用可能状態。nullは判定不能です。</param>
/// <param name="Stable">明示した安定条件が成立し故障区切りを確認できたかです。</param>
public sealed record ProcessSignal(Guid InstanceId, bool? Ready, bool? Healthy, bool Stable = false);
