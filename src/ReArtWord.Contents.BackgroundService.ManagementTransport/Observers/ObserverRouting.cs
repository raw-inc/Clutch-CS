namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

/// <summary>受動監視群から一意の実体・経路だけを選択します。</summary>
public static class ObserverRouting
{
    /// <summary>別NICへの暗黙代替や同名実体の選択を行いません。</summary>
    /// <returns>唯一の受信経路です。</returns>
    public static UdpObserver? Find(IEnumerable<UdpObserver> observers, string clusterId, string peerId)
    {
        ArgumentNullException.ThrowIfNull(observers);
        UdpObserver[] matches = observers.SelectMany(observer => observer.GetClusters()
            .Where(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal)).SelectMany(cluster => cluster.Peers)
            .Where(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal)).Select(_ => observer)).ToArray();
        if (matches.Length == 1 && matches[0].CaptureBindingStatus().Listening)
        {
            return matches[0];
        }
        return null;
    }
}
