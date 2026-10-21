namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>通知から得たクラスター名と実接続portを一組で選択します。</summary>
/// <param name="Name">クラスター名です。</param>
/// <param name="Port">通知された管理portです。</param>
internal sealed record ClusterChoice(string Name, int Port)
{
    /// <summary>同名でportが異なる候補も区別する表示です。</summary>
    public string Label => Name + " · " + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
