using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>OSの収集とCoreが保持する起動時一覧の境界を確認します。</summary>
public sealed class TerminalShellTests
{
    /// <summary>PATHと標準配置を重複なく優先し、サービスPATHにないStore版も候補にします。</summary>
    [Fact]
    public void WindowsPowerShellIncludesStandardAndStoreInstallations()
    {
        using FileTestDirectory files = new();
        string standard = Path.Combine(files.Root, "PowerShell", "7");
        string store = Path.Combine(files.Root, "WindowsApps", "Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe");
        string unrelated = Path.Combine(files.Root, "WindowsApps", "Unrelated_7.0");
        foreach (string directory in new[] { standard, store, unrelated })
        {
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "pwsh.exe");
            File.WriteAllText(executable, "fixture");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        }
        string standardShell = Path.Combine(standard, "pwsh.exe"), storeShell = Path.Combine(store, "pwsh.exe");
        Assert.Equal([storeShell, standardShell], TerminalShellDiscovery.Collect(TerminalShellDiscovery.WindowsCandidates([store], files.Root, files.Root)));
        Assert.Equal([standardShell, storeShell], TerminalShellDiscovery.Collect(TerminalShellDiscovery.WindowsCandidates([], files.Root, files.Root)));
        Assert.Equal([storeShell], TerminalShellDiscovery.Collect(TerminalShellDiscovery.WindowsCandidates([store], Path.Combine(files.Root, "missing"), files.Root)));
        string fallback = Path.Combine(files.Root, "cmd.exe");
        File.Copy(standardShell, fallback);
        File.Delete(standardShell);
        Assert.Equal([fallback, storeShell], TerminalShellDiscovery.Collect(TerminalShellDiscovery.WindowsCandidates([], files.Root, files.Root)));
    }

    /// <summary>実在候補の順序を維持し、重複・相対パス・非実行ファイルを除外します。</summary>
    [Fact]
    public void DiscoveryFiltersCandidatesAndPreservesPriority()
    {
        using FileTestDirectory files = new();
        string first = Path.Combine(files.Root, "first.exe"), second = Path.Combine(files.Root, "second.exe");
        File.WriteAllText(first, "fixture");
        File.WriteAllText(second, "fixture");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(first, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(second, UnixFileMode.UserRead);
            Assert.Equal([first], TerminalShellDiscovery.Collect([first, second]));
            File.SetUnixFileMode(second, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        Assert.Equal([first, second], TerminalShellDiscovery.Collect(["relative.exe", first, first, Path.Combine(files.Root, "missing"), second]));
    }

    /// <summary>収集後に増減した候補は再収集まで反映されず、要求から任意のShellを起動しません。</summary>
    [Fact]
    public void StartupCatalogDoesNotRediscoverOrSubstitute()
    {
        List<string> paths = [Path.GetFullPath("first.exe")];
        TerminalShellCatalog catalog = new(paths);
        paths.Add(Path.GetFullPath("later.exe"));
        Assert.Single(catalog.Shells);
        Assert.Equal(paths[0], catalog.Resolve(null));
        Assert.Equal(paths[0], catalog.Resolve(paths[0]));
        Assert.Throws<ArgumentException>(() => catalog.Resolve(paths[1]));
        Assert.Throws<ArgumentException>(() => catalog.Resolve("first.exe --command something"));
        Assert.Equal(2, new TerminalShellCatalog(paths).Shells.Count);
        Assert.Throws<InvalidOperationException>(() => new TerminalShellCatalog([]).Resolve(null));
    }

    /// <summary>既定要求は旧Coreにも送れる形を維持し、指定時だけShellを含めます。</summary>
    [Fact]
    public void UnspecifiedShellKeepsThePreviousWireContract()
    {
        TerminalRequest request = new(Guid.NewGuid(), Guid.NewGuid());
        using JsonDocument original = JsonSerializer.SerializeToDocument(request, ContractJson.Options);
        Assert.False(original.RootElement.TryGetProperty("shell", out _));
        string path = Path.GetFullPath("chosen.exe");
        using JsonDocument selected = JsonSerializer.SerializeToDocument(request with { Shell = path }, ContractJson.Options);
        Assert.Equal(path, selected.RootElement.GetProperty("shell").GetString());
    }
}
