using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Installer.Views;

/// <summary>ViewModelから依頼されたOS対話だけを実施します。</summary>
internal sealed class DesktopInteraction : IInstallerInteraction
{
    private readonly Window window;

    /// <summary>所有するwindowをdialogの親にします。</summary>
    public DesktopInteraction(Window window) => this.window = window;

    /// <summary>OSの標準folder pickerを開きます。</summary>
    /// <returns>選択pathまたはnullです。</returns>
    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false }).ConfigureAwait(true);
        if (folders.Count == 0)
        {
            return null;
        }
        return folders[0].TryGetLocalPath();
    }

    /// <summary>PNG/ICO用の標準file pickerを開きます。</summary>
    /// <returns>選択pathまたはnullです。</returns>
    public async Task<string?> PickImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("PNG / ICO") { Patterns = ["*.png", "*.ico"] }] }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }
        return files[0].TryGetLocalPath();
    }

    /// <summary>ZIP用の標準ファイル選択を開きます。</summary>
    /// <returns>選択パスまたはnullです。</returns>
    public async Task<string?> PickArchiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Clutch-CS archive (ZIP)") { Patterns = ["*.zip"] }],
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }
        return files[0].TryGetLocalPath();
    }

    /// <summary>明示確認後のWindows再起動を要求します。</summary>
    /// <returns>OSへの要求完了です。</returns>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Use your operating system's restart menu.");
        }
        _ = await BoundedProcessRunner.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), ["/r", "/t", "0"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>配置済みのclient起動入口を明示して開きます。</summary>
    public void OpenClient(string root)
    {
        string path = Path.Combine(root, "Open-Client.cmd");
        if (!OperatingSystem.IsWindows())
        {
            path = Path.Combine(root, "open-client.sh");
        }
        using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden });
    }

    /// <summary>windowを終了します。</summary>
    public void Close() => window.Close();
}
