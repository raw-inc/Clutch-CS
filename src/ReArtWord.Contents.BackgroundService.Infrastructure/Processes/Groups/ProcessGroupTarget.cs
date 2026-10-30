namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>一回の操作で使用する登録版と起動間隔です。</summary>
internal sealed record ProcessGroupTarget(string ApplicationId, string RegistrationVersion, int DelayMilliseconds);
