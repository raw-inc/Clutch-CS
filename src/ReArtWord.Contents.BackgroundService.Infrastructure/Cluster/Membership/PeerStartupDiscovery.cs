using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>新規クラスターを作る前に、同じPeer IDで既に参加している実体がないか確認します。</summary>
internal static class PeerStartupDiscovery
{
    /// <summary>通常の要求を受け付ける前に、生存通知の一周期だけ同名ピアの通知を受け取ります。</summary>
    /// <returns>初回の確認です。後で競合が見つかった場合も通常の受信処理で検知します。</returns>
    internal static async Task ListenAsync(ClusterOptions options, PeerMembership membership, UdpTransport transport, CancellationToken cancellationToken = default)
    {
        transport.Received = (message, address, _) =>
        {
            string hello = "hello";
            if (string.Equals(message.Kind, hello, StringComparison.Ordinal) && string.Equals(message.SenderId, options.PeerId, StringComparison.Ordinal)
                && message.InstanceId != membership.InstanceId && message.Body.Deserialize<PeerAnnouncement>() is { Peer: { } peer } announcement
                && peer.InstanceId == message.InstanceId && string.Equals(peer.PeerId, options.PeerId, StringComparison.Ordinal))
            {
                membership.Observe(announcement, address);
            }
            return Task.CompletedTask;
        };
        transport.Start();
        await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
    }
}
