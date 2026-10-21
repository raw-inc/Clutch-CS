namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>一配送の不足部分と有効期限を保持します。</summary>
/// <param name="First">最初の分割情報。</param>
/// <param name="ExpiresAt">組立期限。</param>
internal sealed record FragmentAssembly(DatagramFragment First, DateTimeOffset ExpiresAt)
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
