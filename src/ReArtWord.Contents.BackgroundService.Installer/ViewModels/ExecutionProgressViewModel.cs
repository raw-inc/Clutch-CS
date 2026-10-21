using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Installer.Localization;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>実行中の工程と転送量を表示し、工程が変わった履歴だけを残します。</summary>
internal sealed partial class ExecutionProgressViewModel : ObservableObject
{
    private static double BytesPerKilobyte => 1024;
    private static double BytesPerMegabyte => BytesPerKilobyte * BytesPerKilobyte;
    private InstallerText text = new(false);

    /// <summary>現在の実測進捗です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Stage)), NotifyPropertyChangedFor(nameof(Steps)),
     NotifyPropertyChangedFor(nameof(Indeterminate)), NotifyPropertyChangedFor(nameof(HasTransfer)), NotifyPropertyChangedFor(nameof(Transfer))]
    public partial OperationProgress Current
    {
        get; set;
    } = new("working");

    /// <summary>展開時に表示する工程履歴です。</summary>
    public ObservableCollection<string> Details
    {
        get;
    } = [];

    /// <summary>現在の作業を選択言語で示します。</summary>
    public string Stage => text["Progress." + Current.Stage];

    /// <summary>対象内の実工程数です。</summary>
    public string Steps => string.Format(CultureInfo.CurrentCulture, text["ProgressPercent"], Current.CompletedSteps / (double)Math.Max(1, Current.TotalSteps));

    /// <summary>工程数がまだ分からない準備処理だけを不定表示にします。</summary>
    public bool Indeterminate => Current.TotalSteps == 0;

    /// <summary>転送している間だけ転送量を表示します。</summary>
    public bool HasTransfer => 0 < Current.TotalBytes;

    /// <summary>送信確認済みのbyte数です。</summary>
    public string Transfer
    {
        get
        {
            double divisor = BytesPerKilobyte;
            string unit = "KB";
            if (BytesPerMegabyte <= Current.TotalBytes)
            {
                divisor = BytesPerMegabyte;
                unit = "MB";
            }
            return string.Format(CultureInfo.CurrentCulture, text["ProgressBytes"], Current.CompletedBytes / divisor, Current.TotalBytes / divisor, unit);
        }
    }

    /// <summary>操作の開始時に表示言語と履歴を固定します。</summary>
    public void Start(InstallerText language)
    {
        text = language;
        Details.Clear();
        Report(new("prepare-helper"));
    }

    /// <summary>量だけの更新を履歴へ重複させず、画面の実測値へ反映します。</summary>
    public void Report(OperationProgress value)
    {
        bool changed = !string.Equals(Current.Stage, value.Stage, StringComparison.Ordinal) || !string.Equals(Current.Target, value.Target, StringComparison.Ordinal);
        Current = value;
        if (changed || Details.Count == 0)
        {
            Details.Add(DateTimeOffset.Now.ToString("T", CultureInfo.CurrentCulture) + "  " + Stage + "  " + value.Target);
        }
    }
}
