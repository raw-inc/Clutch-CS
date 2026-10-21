using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>プレビューと導入で共有する画像の検証とコピーを確認します。</summary>
public sealed class InstallationImagesTests
{
    /// <summary>拡張子に依存せず画像を判別し、設定先へ独立して保存します。</summary>
    /// <returns>読み込みとコピーの検証です。</returns>
    [Fact]
    public async Task CopiesVerifiedImagesIntoTheConfigurationFolder()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string source = Path.Combine(fixture.Root, "source-image.bin");
        byte[] content = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(source, content);
        string configuration = Path.Combine(fixture.Settings.Root, "config");
        Directory.CreateDirectory(configuration);
        InstallationRecord record = new(Guid.NewGuid(), fixture.Settings with { Appearance = new(IconPath: source, FaviconPath: source) }, "fixture", "core.exe", "updater.exe", "client.exe", DateTimeOffset.UtcNow);
        ClientAppearance saved = await InstallationImages.CopyAsync(record);
        Assert.Equal(Path.Combine(configuration, "icon.png"), saved.IconPath);
        Assert.Equal(Path.Combine(configuration, "favicon.png"), saved.FaviconPath);
        File.Delete(source);
        Assert.Equal(content, InstallationImages.Read(saved.IconPath!));
        Assert.Equal(content, InstallationImages.Read(saved.FaviconPath!));
    }

    /// <summary>画像以外や上限を超えるファイルをプレビューへ渡しません。</summary>
    /// <returns>形式とサイズの境界確認です。</returns>
    [Fact]
    public async Task RejectsNonImagesAndOversizedFiles()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string path = Path.Combine(fixture.Root, "image.png");
        await File.WriteAllTextAsync(path, "not an image");
        Assert.Throws<InvalidDataException>(() => InstallationImages.Read(path));
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(InstallationDefaults.MaximumImageBytes + 1);
        }
        Assert.Throws<InvalidDataException>(() => InstallationImages.Read(path));
        Assert.Throws<InvalidDataException>(() => InstallationImages.Read("relative.png"));
    }
}
