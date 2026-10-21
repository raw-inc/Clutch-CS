namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>日時入力の保持数と、実行結果の保存確認を待っている件数。</summary>
/// <param name="LocallyManagedInputs">このPeer実体で保持している日時入力。</param>
/// <param name="UnconfirmedResults">実行結果が得られ、保存確認を待っている入力。</param>
internal sealed record ScheduleRetentionStatus(int LocallyManagedInputs, int UnconfirmedResults);
