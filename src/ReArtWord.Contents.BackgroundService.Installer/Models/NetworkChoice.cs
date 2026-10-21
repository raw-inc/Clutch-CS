using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>選択候補となる一つの実ネットワークです。</summary>
/// <param name="Binding">観測に使う固定経路です。</param>
/// <param name="Label">NICとアドレスの説明です。</param>
internal sealed record NetworkChoice(ClientNetworkBinding Binding, string Label);
