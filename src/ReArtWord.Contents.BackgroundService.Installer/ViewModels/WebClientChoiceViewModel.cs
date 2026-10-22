using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Installer.Services;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>Coreとは独立して選べるWebクライアントの更新対象です。</summary>
internal sealed partial class WebClientChoiceViewModel(ObservedWebClient target, bool japanese) : ObservableObject
{
    /// <summary>確認した導入記録と接続先です。</summary>
    public ObservedWebClient Target
    {
        get;
    } = target;

    /// <summary>対象PCと配置先です。</summary>
    public string Label => Target.DisplayName(japanese);

    /// <summary>今回の更新に含めるかです。</summary>
    [ObservableProperty]
    public partial bool Selected
    {
        get; set;
    }
}
