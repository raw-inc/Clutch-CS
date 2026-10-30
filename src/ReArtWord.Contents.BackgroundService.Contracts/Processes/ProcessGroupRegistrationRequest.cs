namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>確認した版に対するグループの登録・編集要求です。</summary>
/// <param name="Definition">保存する設定です。</param>
/// <param name="ExpectedVersion">新規登録ではnull、編集では確認済みの版です。</param>
public sealed record ProcessGroupRegistrationRequest(ProcessGroupDefinition Definition, string? ExpectedVersion = null);
