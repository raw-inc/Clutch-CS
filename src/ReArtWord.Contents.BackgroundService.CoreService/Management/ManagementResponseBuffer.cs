namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>巨大な応答を最後まで確保する前にJSON出力を止めます。</summary>
internal sealed class ManagementResponseBuffer : MemoryStream
{
    private static int MaximumBytes => 2 * 1024 * 1024;

    /// <summary>未知の大きさを小さい初期バッファーから書き込みます。</summary>
    public ManagementResponseBuffer() : base(8192)
    {
    }

    /// <summary>このバッファー自身の容量制限に達したか。</summary>
    public bool LimitExceeded
    {
        get; private set;
    }

    /// <summary>JSON writerの同期書込みを制限します。</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        base.Write(buffer);
    }

    /// <summary>配列経由の書込みにも同じ上限を適用します。</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(count);
        base.Write(buffer, offset, count);
    }

    /// <summary>一バイト書込みで容量制限を迂回させません。</summary>
    public override void WriteByte(byte value)
    {
        Check(1);
        base.WriteByte(value);
    }

    /// <summary>外側のUDP envelopeを含めた上限へ余白を残します。</summary>
    private void Check(int count)
    {
        if (MaximumBytes - Position < count)
        {
            LimitExceeded = true;
            throw new IOException("管理応答の容量上限に達しました。");
        }
    }
}
