using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>トップからライセンスを読み、言語を保って戻れることを検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ThirdPartyLicenseTests
{
    /// <summary>Core に埋め込む JavaScript の通知と、選択したライセンスを閲覧できます。</summary>
    [Fact]
    public void CatalogIncludesEmbeddedAcornAndSelectedCsvLicense()
    {
        ThirdPartyCatalog catalog = ThirdPartyCatalog.Read();
        ThirdPartyLicense acorn = Assert.Single(catalog.ClutchCs, entry => string.Equals(entry.Name, "Acorn", StringComparison.Ordinal));
        Assert.Equal("MIT", acorn.License);
        Assert.Contains("Permission is hereby granted", acorn.Text, StringComparison.Ordinal);
        ThirdPartyLicense csv = Assert.Single(catalog.ClutchCs, entry => string.Equals(entry.Name, "CsvHelper", StringComparison.Ordinal));
        Assert.Equal("MS-PL OR Apache-2.0", csv.License);
        Assert.Equal("Apache-2.0", csv.SelectedLicense);
    }

    /// <summary>言語を切り替えても、両画面で同じ採用条件を読み取れます。</summary>
    [Fact]
    public void LicenseSelectionFollowsTheDisplayLanguage()
    {
        LicensesViewModel model = new() { Japanese = true };
        List<string?> changes = [];
        model.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName);
        Assert.Contains("採用ライセンス: Apache-2.0", model.ClutchCsNotices, StringComparison.Ordinal);
        Assert.Contains("Zstandard: 採用ライセンス: BSD-3-Clause", model.ClutchCsNotices, StringComparison.Ordinal);
        Assert.Contains("Zstandard: 採用ライセンス: BSD-3-Clause", model.InstallerNotices, StringComparison.Ordinal);
        model.Japanese = false;
        Assert.Contains(nameof(model.ClutchCsNotices), changes);
        Assert.Contains(nameof(model.InstallerNotices), changes);
        Assert.Contains("Selected license: Apache-2.0", model.ClutchCsNotices, StringComparison.Ordinal);
        Assert.Contains("Zstandard: Selected license: BSD-3-Clause", model.ClutchCsNotices, StringComparison.Ordinal);
        Assert.Contains("Zstandard: Selected license: BSD-3-Clause", model.InstallerNotices, StringComparison.Ordinal);
    }

    /// <summary>両製品の一覧と全文を、キーボードで開いた画面から閲覧できます。</summary>
    /// <returns>両言語での遷移とスクロールの確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WelcomeLinkOpensTabsAndBackPreservesLanguage(int language)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch<bool>(() =>
        {
            InstallationViewModel settings = new();
            WizardViewModel model = new(settings, new(settings), new TestInteraction()) { LanguageIndex = language };
            InstallerWindow window = new() { DataContext = model };
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Button link = window.FindControl<Button>("ThirdPartyLink")!;
            Assert.True(link.IsEffectivelyVisible);
            link.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.Licenses.IsOpen);
            Assert.False(window.FindControl<Grid>("WizardContent")!.IsEffectivelyVisible);
            ThirdPartyLicensesView page = window.FindControl<ThirdPartyLicensesView>("ThirdPartyPage")!;
            Assert.True(page.IsEffectivelyVisible);
            TabControl tabs = page.FindControl<TabControl>("LicenseTabs")!;
            Assert.Equal(settings.Licenses.Text["InstallerTab"], Assert.IsType<TabItem>(tabs.Items[0]).Header);
            Assert.Equal(2, tabs.ItemCount);
            for (int index = 0; index < tabs.ItemCount; index++)
            {
                tabs.SelectedIndex = index;
                Dispatcher.UIThread.RunJobs();
                TextBox text = Assert.IsType<TextBox>(Assert.IsType<TabItem>(tabs.Items[index]).Content);
                ThirdPartyCatalog catalog = ThirdPartyCatalog.Read();
                IReadOnlyList<ThirdPartyLicense> entries = catalog.Installer;
                if (index != 0)
                {
                    entries = catalog.ClutchCs;
                }
                Assert.NotEmpty(entries);
                foreach (ThirdPartyLicense entry in entries)
                {
                    Assert.Contains(entry.Name + " " + entry.Version, text.Text, StringComparison.Ordinal);
                    Assert.Contains(entry.Text, text.Text, StringComparison.Ordinal);
                }
                Assert.True(window.ClientSize.Width / 2 < text.Bounds.Width);
                Capture(window, $"installer-licenses-{language}-{index}.png");
                ScrollViewer scroll = text.GetVisualDescendants().OfType<ScrollViewer>().Single();
                Assert.True(scroll.Viewport.Height < scroll.Extent.Height);
                scroll.ScrollToEnd();
                Dispatcher.UIThread.RunJobs();
                Assert.True(0 < scroll.Offset.Y);
                Capture(window, $"installer-licenses-end-{language}-{index}.png");
            }
            Button back = page.FindControl<Button>("LicensesBackButton")!;
            Assert.True(back.IsEffectivelyVisible);
            back.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.Licenses.IsOpen);
            Assert.True(link.IsEffectivelyVisible);
            Assert.Equal(EWizardPage.Language, model.Page);
            Assert.Equal(language, model.LanguageIndex);
            Assert.Empty(model.Discovery.Targets);
            Assert.False(settings.Accepted);
            Capture(window, $"installer-license-welcome-{language}.png");
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    /// <summary>指定された場合だけ描画結果を保存します。</summary>
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("CLUTCH_CAPTURE_DIRECTORY");
        if (directory is null)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
