namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>CLI・Installer・Coreで共有するアーカイブ転送の上限です。</summary>
public static class ArchiveTransferLimits
{
    /// <summary>一回の転送上限は1GiBです。</summary>
    public static long MaximumBytes => 1073741824;

    /// <summary>管理通信1回あたりに転送する最大バイト数。</summary>
    public static int ChunkBytes => 49152;
}
