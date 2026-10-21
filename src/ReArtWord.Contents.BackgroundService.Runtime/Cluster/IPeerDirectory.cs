using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Runtime.Cluster;

/// <summary>実行対象の解決に必要な確認済み参加情報です。</summary>
public interface IPeerDirectory
{
    /// <summary>このPeerの識別子です。</summary>
    public string LocalPeerId
    {
        get;
    }

    /// <summary>今回のプロセス実体です。</summary>
    public Guid LocalInstanceId
    {
        get;
    }

    /// <summary>現在確認できるCoordinatorです。</summary>
    public string? CoordinatorId
    {
        get;
    }

    /// <summary>接続・同期状態を含むPeer一覧を取得します。</summary>
    /// <returns>この時点の一覧です。</returns>
    public IReadOnlyList<PeerInfo> GetPeers();

    /// <summary>平らなHost Groupの所属を取得します。</summary>
    /// <returns>Group名からHost識別子集合への対応です。</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups();
}
