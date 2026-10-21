using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>確認した版に対するプロセス登録の変更です。</summary>
/// <param name="Definition">明示する管理範囲。</param>
/// <param name="ExpectedVersion">更新前の版。新規はnull。</param>
public sealed record ProcessRegistrationRequest(ProcessDefinition Definition, string? ExpectedVersion = null);
