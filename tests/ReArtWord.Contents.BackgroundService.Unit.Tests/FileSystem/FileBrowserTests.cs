using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Browsing;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>CoreのOSアクセス方式と内部の限定領域方式で、閲覧範囲とリンク拒否を確認します。</summary>
public sealed class FileBrowserTests
{
    /// <summary>内部の限定領域方式では、ドライブ参照がファイル操作の許可を拡大しません。</summary>
    [Fact]
    public void DriveBrowsingLeavesTransferPolicyUnchanged()
    {
        using FileTestDirectory fixture = new();
        FileBrowser browser = new(fixture.Options);
        FileBrowseResult drives = browser.Browse(new(FromDrives: true));
        Assert.Null(drives.Problem);
        Assert.Contains(drives.Entries, entry => string.Equals(entry.Path, Path.GetPathRoot(fixture.Root), StringComparison.OrdinalIgnoreCase));
        string parent = Path.GetDirectoryName(fixture.Root)!;
        FileBrowseResult listing = browser.Browse(new(parent, FromDrives: true));
        Assert.Null(listing.Problem);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Path, fixture.Root, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(browser.Browse(new(parent)).Problem);
        FileAccessPolicy policy = new(fixture.Options);
        Assert.Throws<UnauthorizedAccessException>(() => policy.ResolvePath(parent, true));
    }

    /// <summary>内部の限定領域方式では、親への移動で許可ルートを越えず、ファイル内容を取得しません。</summary>
    [Fact]
    public void ShowsAllowedRootsAndDistinguishesFilesFromDirectories()
    {
        using FileTestDirectory fixture = new();
        FileBrowser browser = new(fixture.Options);
        string file = Path.Combine(fixture.Root, "example.txt");
        File.WriteAllText(file, "content");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "folder"));
        Assert.Equal(fixture.Root, Assert.Single(browser.Browse(new()).Entries).Path);
        FileBrowseResult listing = browser.Browse(new(fixture.Root));
        Assert.Null(listing.Problem);
        Assert.Null(listing.Parent);
        Assert.Contains(listing.Entries, item => string.Equals(item.Name, "example.txt", StringComparison.Ordinal) && !item.IsDirectory);
        Assert.Contains(listing.Entries, item => string.Equals(item.Name, "folder", StringComparison.Ordinal) && item.IsDirectory);
        FileBrowseResult child = browser.Browse(new(Path.Combine(fixture.Root, "folder")));
        Assert.Equal(fixture.Root, child.Parent);
        Assert.NotNull(browser.Browse(new(Path.Combine(fixture.Root, ".."))).Problem);
        Assert.Empty(browser.Browse(new(Path.Combine(fixture.Root, ".."))).Entries);
    }

    /// <summary>大きなディレクトリは有限ページになり、次ページで残りを確認できます。</summary>
    [Fact]
    public void PagesLargeDirectoriesAndReportsMissingPaths()
    {
        using FileTestDirectory fixture = new();
        FileBrowser browser = new(fixture.Options);
        for (int index = 0; index < 140; index++)
        {
            File.WriteAllText(Path.Combine(fixture.Root, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), string.Empty);
        }
        FileBrowseResult first = browser.Browse(new(fixture.Root));
        Assert.Equal(128, first.Entries.Count);
        Assert.NotNull(first.NextOffset);
        FileBrowseResult second = browser.Browse(new(fixture.Root, first.NextOffset.Value));
        Assert.Equal(12, second.Entries.Count);
        Assert.Equal(140, first.Entries.Concat(second.Entries).Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Null(second.NextOffset);
        Assert.NotNull(browser.Browse(new(Path.Combine(fixture.Root, "missing"))).Problem);
    }

    /// <summary>OS権限で直接読める旧roots外のパスも、リンクとその子を経由した参照は拒否します。</summary>
    /// <returns>実junction／symlinkを使った通常参照とドライブ参照の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatingSystemAccessRejectsLinkedPathsAsync(bool fromDrives)
    {
        using FileTestDirectory fixture = new();
        string configuredRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "configured")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-roots", "child")).FullName;
        string file = Path.Combine(target, "example.txt");
        await File.WriteAllTextAsync(file, "content").ConfigureAwait(true);
        string link = Path.Combine(fixture.Root, "linked");
        await FileTestDirectory.CreateDirectoryLinkAsync(link, Path.GetDirectoryName(target)!).ConfigureAwait(true);
        try
        {
            FileBrowser browser = new(fixture.Options with { AllowedRoots = [configuredRoot], UseOperatingSystemAccess = true });
            FileBrowseResult direct = browser.Browse(new(target, FromDrives: fromDrives));
            Assert.Null(direct.Problem);
            Assert.Equal(file, Assert.Single(direct.Entries).Path);
            FileBrowseResult listing = browser.Browse(new(fixture.Root, FromDrives: fromDrives));
            Assert.Null(listing.Problem);
            Assert.DoesNotContain(listing.Entries, entry => string.Equals(entry.Path, link, StringComparison.OrdinalIgnoreCase));
            foreach (string path in new[] { link, Path.Combine(link, "child") })
            {
                FileBrowseResult rejected = browser.Browse(new(path, FromDrives: fromDrives));
                Assert.NotNull(rejected.Problem);
                Assert.Empty(rejected.Entries);
            }
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
