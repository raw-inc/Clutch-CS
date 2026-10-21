using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Installer.Services;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>観測した一つのピアの選択状態です。</summary>
internal sealed partial class TargetChoiceViewModel : ObservableObject
{
    private readonly bool japanese;

    /// <summary>操作対象の実体を保持します。</summary>
    public TargetChoiceViewModel(ObservedTarget target, bool japanese)
    {
        Target = target;
        this.japanese = japanese;
    }

    /// <summary>観測時の固定対象です。</summary>
    public ObservedTarget Target
    {
        get;
    }

    /// <summary>対象の識別情報です。</summary>
    public string Label => Target.DisplayName(japanese);

    /// <summary>実行対象に含めるかです。</summary>
    [ObservableProperty]
    public partial bool Selected
    {
        get; set;
    }
}
