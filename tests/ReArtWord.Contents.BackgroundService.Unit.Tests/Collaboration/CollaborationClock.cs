namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>表示保持期限を実時間待ちなしで確認します。</summary>
internal sealed class CollaborationClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    /// <summary>現在の試験時刻です。</summary>
    /// <returns>UTC時刻。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>期限を進めます。</summary>
    public void Advance(TimeSpan elapsed) => now += elapsed;
}
