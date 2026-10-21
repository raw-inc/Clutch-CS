using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReArtWord.Contents.BackgroundService.Installer.Localization;
using ReArtWord.Contents.BackgroundService.Installer.Models;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>トップ画面から開くサードパーティライセンスです。</summary>
internal sealed partial class LicensesViewModel : ObservableObject
{
    private readonly ThirdPartyCatalog catalog;

    /// <summary>同梱したライセンスを製品ごとに用意します。</summary>
    public LicensesViewModel()
    {
        catalog = ThirdPartyCatalog.Read();
    }

    /// <summary>ライセンス画面を表示中かです。</summary>
    [ObservableProperty]
    public partial bool IsOpen
    {
        get; set;
    }

    /// <summary>日本語を選択しているかです。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text), nameof(InstallerNotices), nameof(ClutchCsNotices))]
    public partial bool Japanese
    {
        get; set;
    }

    /// <summary>現在の表示言語です。</summary>
    public InstallerText Text => new(Japanese);

    /// <summary>インストーラーのライセンス一覧です。</summary>
    public string InstallerNotices => Format(catalog.Installer, Text["SelectedLicense"]);

    /// <summary>Clutch-CSのライセンス一覧です。</summary>
    public string ClutchCsNotices => Format(catalog.ClutchCs, Text["SelectedLicense"]);

    /// <summary>ライセンス画面を開きます。</summary>
    [RelayCommand]
    private void Open() => IsOpen = true;

    /// <summary>トップ画面へ戻ります。</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>名称順に並べ、原文が同一の項目はまとめて掲載します。</summary>
    /// <returns>タブ内を続けて読めるライセンス一覧です。</returns>
    private static string Format(IReadOnlyList<ThirdPartyLicense> entries, string selectionLabel)
    {
        return string.Join(Environment.NewLine + Environment.NewLine, entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Version, StringComparer.Ordinal).GroupBy(entry => entry.Text, StringComparer.Ordinal).Select(group =>
            string.Join(Environment.NewLine, group.Select(entry => FormatHeading(entry, selectionLabel))) +
            Environment.NewLine + Environment.NewLine + group.Key));
    }

    /// <summary>ライブラリ名と採用したライセンスを表示します。</summary>
    /// <returns>ライセンス本文の見出しです。</returns>
    private static string FormatHeading(ThirdPartyLicense entry, string selectionLabel)
    {
        List<string> lines = [entry.Name + " " + entry.Version + " (" + entry.License + ")"];
        if (!string.IsNullOrWhiteSpace(entry.SelectedLicense))
        {
            lines.Add(selectionLabel + ": " + entry.SelectedLicense);
        }
        foreach (ThirdPartyLicenseSelection selection in entry.LicenseSelections ?? [])
        {
            lines.Add(selection.Name + ": " + selectionLabel + ": " + selection.SelectedLicense + " (" + selection.License + ")");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
