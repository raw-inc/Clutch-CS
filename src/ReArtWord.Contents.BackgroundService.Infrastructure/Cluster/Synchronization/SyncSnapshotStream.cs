namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>serialize中にも作業ファイルの容量上限を超えて書き込みません。</summary>
internal sealed class SyncSnapshotStream : FileStream
{
    private readonly long maximumBytes;

    /// <summary>この同期だけの新規作業ファイルを作成します。</summary>
    internal SyncSnapshotStream(string path, long maximumBytes)
        : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        this.maximumBytes = maximumBytes;
    }

    /// <summary>配列書込みにも同じ上限を適用します。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(count);
        base.Write(buffer, offset, count);
    }

    /// <summary>span書込みにも同じ上限を適用します。</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        base.Write(buffer);
    }

    /// <summary>単一バイトでも作業容量を超えません。</summary>
    public override void WriteByte(byte value)
    {
        Check(1);
        base.WriteByte(value);
    }

    /// <summary>非同期serializeを上限内のファイルへ接続します。</summary>
    /// <returns>実ファイル書込みの完了です。</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    /// <summary>配列の非同期書込みも同じ上限で検証します。</summary>
    /// <returns>実ファイル書込みの完了です。</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        Check(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    /// <summary>許可容量から溢れる前に同期を中断します。</summary>
    private void Check(int count)
    {
        if (maximumBytes - Position < count)
        {
            throw new SyncFailureException("Sync.SnapshotCapacity");
        }
    }
}
