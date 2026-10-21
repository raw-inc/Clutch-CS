using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>PC排他内で使う、確認・永続開始・再確認・一回実行の境界です。</summary>
internal sealed class IdleShutdownController(Func<DeviceIdleShutdownPolicy> read, Func<bool?> supply, Func<CancellationToken, Task<DeviceUserActivity?>> activity,
    Func<string, bool> claim, Func<CancellationToken, Task<NodeResult>> execute, TimeProvider clock)
{
    /// <summary>永続記録に失敗した要求、入力が発生した要求、受付済み要求を送信しません。</summary>
    /// <returns>今回実行した場合だけOS受付結果です。</returns>
    internal async Task<NodeResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        string? episode = IdleShutdownDecision.Episode(read(), supply(), await activity(cancellationToken).ConfigureAwait(false), clock.GetUtcNow());
        if (episode is null || !claim(episode))
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Recheck input after persisting the attempt; uncertain or newly active sessions cancel this attempt.
        string? confirmed = IdleShutdownDecision.Episode(read(), supply(), await activity(cancellationToken).ConfigureAwait(false), clock.GetUtcNow());
        if (!string.Equals(episode, confirmed, StringComparison.Ordinal))
        {
            return null;
        }
        return await execute(cancellationToken).ConfigureAwait(false);
    }
}
