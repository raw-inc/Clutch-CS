namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>PC内のWebクライアントだけを削除する、確認済みの操作です。</summary>
public sealed record WebClientRemovalJob(Guid OperationId, WebClientInstallation Installation, bool KeepConfiguration);
