namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>編集UIの頻度と保持期限を実時間待ちなしで確認します。</summary>
internal sealed class EditorTestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    /// <summary>現在の試験時刻です。</summary>
    /// <returns>UTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>試験の経過時間を進めます。</summary>
    internal void Advance(TimeSpan elapsed) => now += elapsed;
}
