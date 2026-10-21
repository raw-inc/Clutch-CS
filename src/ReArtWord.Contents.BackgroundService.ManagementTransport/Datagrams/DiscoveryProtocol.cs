namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>管理portが異なるクラスタも受動発見できる共通の通知先です。</summary>
public static class DiscoveryProtocol
{
    /// <summary>既定管理portと共用する、通知だけの受信先です。</summary>
    public static int Port => 47740;

    /// <summary>通常のPeer参加処理へ渡さない発見専用の種類です。</summary>
    public static string AnnouncementKind => "discovery";

    /// <summary>通常通知の成功後、異なる管理portの存在情報だけを共通受信先にも配信します。</summary>
    /// <returns>通常の参加通知と、発見用通知の送信試行完了です。</returns>
    public static async Task AnnounceAsync(UdpTransport transport, WireMessage message, int managementPort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(message);
        await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (managementPort == Port)
        {
            return;
        }
        try
        {
            await transport.SendToPortAsync(message with { Id = Guid.NewGuid(), Kind = AnnouncementKind }, Port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
        {
            // 発見専用の通知失敗で、通常のクラスタ通信が成立しているPeerをUnavailableにしません。
            System.Diagnostics.Trace.TraceWarning("Cluster discovery announcement could not be sent: {0}", exception.Message);
        }
    }
}
