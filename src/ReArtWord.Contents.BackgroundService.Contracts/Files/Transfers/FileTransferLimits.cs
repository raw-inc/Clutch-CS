namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>通信と受信ファイル処理で共通の容量上限です。</summary>
public static class FileTransferLimits
{
    /// <summary>従来の管理UDP経路で一度に配送するバイト数です。</summary>
    public static int DatagramBytes => 32768;

    /// <summary>信頼できる順序付き通信で一度に検証・保存するバイト数です。</summary>
    public static int StreamBytes => 1024 * 1024;
}
