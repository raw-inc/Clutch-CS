namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>ピア自身のNIC識別子とIPv4割当です。</summary>
internal sealed record SettingNetworkInterface(string Id, string Name, IReadOnlyList<string> Addresses, string? Status = null)
{
    /// <summary>稼働していないと確認できたNICを、状態未取得の旧Peerと区別します。</summary>
    internal bool Unavailable => Status is not null && !string.Equals(Status, "Up", StringComparison.OrdinalIgnoreCase);

    /// <summary>NIC名と状態を一緒に表示し、保存済み識別子は変換しません。</summary>
    /// <returns>共通・PC別設定で使用する同じ表示名です。</returns>
    internal string DisplayName(Localization.UiText ui)
    {
        string label = "Network status unverified";
        if (Status is not null)
        {
            label = Unavailable ? "Network adapter unavailable"
                                : "Network adapter up";
        }
        return string.Concat(Name, " · ", ui[label], " · ", Id);
    }
}
