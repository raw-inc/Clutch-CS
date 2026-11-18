using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>PC排他内で使う、確認・永続開始・再確認・一回実行の境界です。</summary>
internal sealed class IdleShutdownController(Func<DeviceIdleShutdownPolicy> read, Func<bool?> supply, Func<CancellationToken, Task<DeviceUserActivity?>> activity,
    Func<string, bool> claim, Func<CancellationToken, Task<NodeResult>> execute, TimeProvider clock, Func<DeviceIdleShutdownPolicy, TimeSpan?> enableAge)
{
    private Guid version;
    private long policySince;
    private string? input;
    private long unchangedSince;

    /// <summary>永続記録に失敗した要求、入力が発生した要求、受付済み要求を送信しません。</summary>
    /// <returns>今回実行した場合だけOS受付結果です。</returns>
    internal async Task<NodeResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        string? episode = Episode(read(), supply(), await activity(cancellationToken).ConfigureAwait(false));
        if (episode is null || !claim(episode))
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 停止要求を記録した後にもう一度入力を確認し、途中で操作されたPCを停止させないようにします。
        string? confirmed = Episode(read(), supply(), await activity(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(episode, confirmed, StringComparison.Ordinal))
        {
            return null;
        }
        return await execute(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>設定の有効化と、全セッションの入力が変わらずに経過した時間を別々に確認します。</summary>
    /// <returns>二つの待機条件を満たした期間の識別子です。</returns>
    private string? Episode(DeviceIdleShutdownPolicy policy, bool? ac, DeviceUserActivity? sample)
    {
        long now = clock.GetTimestamp();
        if (version != policy.Version)
        {
            version = policy.Version;
            policySince = now;
        }
        string? observed = sample?.InputSample ?? sample?.Identity;
        if (observed is null || !string.Equals(input, observed, StringComparison.Ordinal))
        {
            input = observed;
            unchangedSince = now;
        }
        TimeSpan enabled = enableAge(policy) ?? clock.GetElapsedTime(policySince, now);
        TimeSpan idle = sample?.IdleFor ?? clock.GetElapsedTime(unchangedSince, now);
        return IdleShutdownDecision.Episode(policy, ac, sample, enabled, idle);
    }
}
