using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;

/// <summary>更新後の健康確認と停止確認に使える実識別子です。</summary>
/// <param name="Ready">新起動回のreadyを確認できたかです。</param>
/// <param name="InstanceId">確認した新しい実体です。</param>
/// <param name="Process">取得できた実プロセスです。</param>
internal sealed record UpdateHealthResult(bool Ready, Guid? InstanceId, ProcessIdentity? Process);
