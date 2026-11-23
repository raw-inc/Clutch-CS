namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>一配送の不足部分と保持期間の起点を記録します。</summary>
/// <param name="First">最初の分割情報。</param>
/// <param name="ReceivedAt">最初の断片を受信したときの単調時計の値。</param>
internal sealed record FragmentAssembly(DatagramFragment First, long ReceivedAt)
{
    /// <summary>先頭で確認できた、ACKを必要としない一時通知です。</summary>
    public bool Transient
    {
        get; set;
    }

    /// <summary>到着した部分です。</summary>
    public Dictionary<int, byte[]> Parts
    {
        get;
    } = [];
}
