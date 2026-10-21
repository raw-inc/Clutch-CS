namespace ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;

/// <summary>同じ借用領域へcodecごとのaccess unitを復元します。</summary>
public interface IDesktopRtpAssembler
{
    /// <summary>次のAppendまで有効な完成フレームです。</summary>
    public ReadOnlyMemory<byte> Frame
    {
        get;
    }

    /// <summary>独立して復号を再開できるフレームか。</summary>
    public bool KeyFrame
    {
        get;
    }

    /// <summary>一つのRTP packetを消費します。</summary>
    /// <returns>復号可能なフレームが完成したか。</returns>
    public bool Append(ReadOnlySpan<byte> packet);
}
