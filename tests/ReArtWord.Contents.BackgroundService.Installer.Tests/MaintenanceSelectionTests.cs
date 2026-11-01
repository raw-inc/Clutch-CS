using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Installer.Views;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>保守方式の選択を、同じ画面内の選択肢だけで管理することを確認します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class MaintenanceSelectionTests
{
    /// <summary>画面を表示する順番にかかわらず、内部設定と選択表示が一致します。</summary>
    /// <returns>隔離された画面の確認です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task TwoWindowsKeepTheirOwnSelectionAsync(int language, bool repair)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(InstallerTestApp));
        await session.Dispatch(() =>
        {
            InstallationViewModel firstSettings = new() { Operation = EInstallerOperation.Update };
            InstallationViewModel secondSettings = new() { Operation = EInstallerOperation.Update, UpdateMode = EInstallerUpdateMode.Fleet };
            secondSettings.Repair.Mode = EInstallerRepairMode.Archive;
            EInstallerOperation operation = EInstallerOperation.Update;
            if (repair)
            {
                operation = EInstallerOperation.Repair;
            }
            WizardViewModel firstModel = new(firstSettings, new(firstSettings), new TestInteraction()) { LanguageIndex = language, OperationIndex = (int)operation, Page = EWizardPage.Maintenance };
            WizardViewModel secondModel = new(secondSettings, new(secondSettings), new TestInteraction()) { LanguageIndex = language, OperationIndex = (int)operation, Page = EWizardPage.Maintenance };

            // 両方の設定を表示前に反映し、未表示の画面同士でも選択が干渉しないことを確認します。
            InstallerWindow first = new() { DataContext = firstModel };
            InstallerWindow second = new() { DataContext = secondModel };
            try
            {
                first.Show();
                second.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.True(firstSettings.SkipActive);
                Assert.True(secondSettings.Fleet);
                Assert.True(firstSettings.Repair.Automatic);
                Assert.True(secondSettings.Repair.RestoreArchive);
                if (repair)
                {
                    AssertSelection(first, EInstallerRepairMode.Automatic);
                    AssertSelection(second, EInstallerRepairMode.Archive);
                }
                else
                {
                    AssertSelection(first, EInstallerUpdateMode.SkipActive);
                    AssertSelection(second, EInstallerUpdateMode.Fleet);
                }
            }
            finally
            {
                first.Close();
                second.Close();
            }
        }, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>設定に対応する選択肢だけが、同じ親要素内で選択されていることを確認します。</summary>
    private static void AssertSelection(InstallerWindow window, object mode)
    {
        RadioButton selected = window.GetVisualDescendants().OfType<RadioButton>().Single(button => Equals(button.CommandParameter, mode));
        Assert.True(selected.IsChecked);
        Panel parent = Assert.IsAssignableFrom<Panel>(selected.Parent);
        Assert.Same(selected, Assert.Single(parent.Children.OfType<RadioButton>(), button => button.IsChecked == true));
    }
}
