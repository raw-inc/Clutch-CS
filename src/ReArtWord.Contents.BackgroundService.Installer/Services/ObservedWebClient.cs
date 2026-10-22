using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>Webクライアントの配置と、接続先の場合だけ使用するCoreを固定します。</summary>
/// <param name="Installation">確認した導入記録です。</param>
/// <param name="Core">接続先のCoreです。このPCのクライアントはnullです。</param>
internal sealed record ObservedWebClient(WebClientInstallation Installation, ObservedTarget? Core = null)
{
    /// <summary>Coreと区別してPC、ポート番号、配置先を表示します。</summary>
    /// <returns>対象選択と結果表示に共通の名称です。</returns>
    internal string DisplayName(bool japanese)
    {
        string host = "This PC";
        if (japanese)
        {
            host = "このPC";
        }
        host = Core?.Peer?.HostId ?? host;
        return host + " / WebClient · " + Installation.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine + Installation.Root;
    }
}
