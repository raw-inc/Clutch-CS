namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Streams;

/// <summary>フレーム境界の正常終了と、途中で切れたヘッダーを区別します。</summary>
public static class FrameReader
{
    /// <summary>先頭でのEOFだけを正常終了とし、部分受信や取消しは呼出元へ返します。</summary>
    /// <returns>完全なヘッダーを受信した場合はtrue、次のフレームがない場合はfalseです。</returns>
    public static async ValueTask<bool> ReadHeaderAsync(Stream stream, Memory<byte> header, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (header.IsEmpty)
        {
            throw new ArgumentException("A frame header must not be empty.", nameof(header));
        }
        int count = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (count != 0 && count != header.Length)
        {
            throw new EndOfStreamException("The frame header ended before it was complete.");
        }
        return count != 0;
    }
}
