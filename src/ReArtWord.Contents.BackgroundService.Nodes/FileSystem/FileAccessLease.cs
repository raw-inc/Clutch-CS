namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>取得した競合範囲を所有期間の終了時に解放します。</summary>
internal sealed class FileAccessLease : IDisposable
{
    private Action? release;

    /// <summary>一度だけ呼ぶ解放処理を受け取ります。</summary>
    internal FileAccessLease(Action release)
    {
        this.release = release;
    }

    /// <summary>競合範囲の所有を解放します。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
