namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>編集時の比較保存に使う版と、現在のグループ状態です。</summary>
/// <param name="Version">確認した保存版です。</param>
/// <param name="Definition">登録設定です。</param>
/// <param name="Status">現在の操作状態です。</param>
public sealed record RegisteredProcessGroup(string Version, ProcessGroupDefinition Definition, ProcessGroupStatus Status);
