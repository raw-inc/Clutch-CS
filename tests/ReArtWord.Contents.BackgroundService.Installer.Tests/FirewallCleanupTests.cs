using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>アンインストール後の Firewall 記録が、次のインストールを妨げないことを確認します。</summary>
public sealed class FirewallCleanupTests
{
    /// <summary>削除済みの導入先を、一時ルールの解除時に作り直しません。</summary>
    /// <returns>OS の Firewall を操作せず、記録先だけを確認します。</returns>
    [Fact]
    public async Task ResultRuleCleanupKeepsItsFilesOutsideTheRemovedInstallation()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        InstallationRecord record = Record(fixture);
        Guid operation = Guid.NewGuid();
        string workspace = SetupWorkspace.DirectoryFor(operation);
        try
        {
            string first = await WindowsFirewallFiles.WriteAsync(record, false, operation);
            Directory.CreateDirectory(record.Settings.Root);
            Directory.Delete(record.Settings.Root);
            string cleanup = await WindowsFirewallFiles.WriteAsync(record, true, operation);
            Assert.False(Directory.Exists(record.Settings.Root));
            Assert.True(UpdateLayout.Contains(workspace, first));
            Assert.True(UpdateLayout.Contains(workspace, cleanup));
            Assert.True(File.Exists(Path.ChangeExtension(cleanup, ".ps1")));
        }
        finally
        {
            await InstallationFiles.DeleteDirectoryAsync(Path.GetDirectoryName(workspace)!, Path.GetFileName(workspace), CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>既存の解除記録だけなら保持して導入でき、未知のファイルがあれば副作用前に止めます。</summary>
    /// <returns>通常の導入と記録の保持、既存ファイルの保護の確認です。</returns>
    [Theory]
    [InlineData("cleanup", true)]
    [InlineData("script", false)]
    [InlineData("json", false)]
    [InlineData("extra", false)]
    [InlineData("installation", false)]
    public async Task InstallationPreservesCleanupLogsAndRejectsUnrelatedContent(string change, bool accepted)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        fixture.Platform.UseFilePermissions = OperatingSystem.IsWindows();
        string specification = await LegacyCleanupAsync(fixture);
        switch (change)
        {
            case "script": await File.AppendAllTextAsync(Path.ChangeExtension(specification, ".ps1"), "# changed"); break;
            case "json": await File.WriteAllTextAsync(specification, "{}"); break;
            case "extra": await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(specification)!, "user.txt"), "retained"); break;
            case "installation": await File.WriteAllTextAsync(Path.Combine(fixture.Settings.Root, InstallationFiles.RecordName), "retained"); break;
        }
        Dictionary<string, byte[]> original = Directory.GetFiles(fixture.Settings.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        Assert.Equal(accepted, InstallationPolicy.CanInstallInto(fixture.Settings.Root));
        SetupResult result = await fixture.RunAsync(new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings), "install");
        if (accepted)
        {
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.True(File.Exists(Path.Combine(fixture.Settings.Root, "core", "core.exe")));
        }
        else
        {
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.Equal("validate", result.Stage);
            Assert.Empty(fixture.Platform.Calls);
            Assert.False(Directory.Exists(Path.Combine(fixture.Settings.Root, "core")));
        }
        foreach (KeyValuePair<string, byte[]> item in original)
        {
            Assert.Equal(item.Value, await File.ReadAllBytesAsync(item.Key));
        }
    }

    /// <summary>クライアントを準備する前に、既存の導入先を選択言語で案内します。</summary>
    /// <returns>確認画面へ進む前の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDestinationIsReportedBeforeReview(bool japanese)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        Directory.CreateDirectory(fixture.Settings.Root);
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.Root, "user.txt"), "retained");
        InstallationViewModel state = new()
        {
            Advanced = true, PerUser = true, Root = fixture.Settings.Root, Package = fixture.Settings.PackageRoot,
            Accepted = true, LicenseRead = true, Japanese = japanese, NewCluster = true,
        };
        state.Components.Install = false;
        state.Networks.Add(fixture.Settings.Network);
        IOException error = await Assert.ThrowsAsync<IOException>(() => WizardExecution.PrepareAsync(state));
        string expected = "The installation folder contains existing files.";
        if (japanese)
        {
            expected = "インストール先に既存のファイルがあります。";
        }
        Assert.StartsWith(expected, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Platform.ClientRoot));
        Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(fixture.Settings.Root, "user.txt")));
    }

    /// <summary>Core 側の失敗で、起動確認済みクライアントの案内を失いません。</summary>
    /// <returns>導入済みクライアントの起動先も確認します。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, "サービス：完了できませんでした。")]
    [InlineData(EOutcome.Unknown, "サービス：結果を確認できませんでした。")]
    public async Task PartialInstallReportsTheReadyClientWithoutClaimingCoreSuccess(EOutcome outcome, string expected)
    {
        InstallationViewModel state = new() { Japanese = true };
        WebClientInstallation client = new(Guid.NewGuid(), Path.GetTempPath(), "client.exe", WebClientSettings.DefaultPort, true);
        SetupResult failed = new(Guid.NewGuid(), outcome, "validate", "fixture", Path.GetTempPath());
        Models.WizardResult result = WizardExecution.InstallResult(state, failed, client);
        Assert.False(state.Installed);
        Assert.Equal(client, result.WebClient);
        Assert.Contains(expected, result.Summary, StringComparison.Ordinal);
        state.Components.InstalledClient = result.WebClient;
        TestInteraction interaction = new();
        await state.Components.OpenAsync(interaction);
        Assert.Equal(client.Root, interaction.OpenedClientRoot);
    }

    /// <summary>以前の版が導入先へ残した解除記録を、試験専用フォルダーに再現します。</summary>
    /// <returns>保存した設定ファイルのパスです。</returns>
    private static async Task<string> LegacyCleanupAsync(SetupFixture fixture, CancellationToken cancellationToken = default)
    {
        Guid operation = Guid.NewGuid();
        string workspace = SetupWorkspace.DirectoryFor(operation);
        try
        {
            string specification = await WindowsFirewallFiles.WriteAsync(Record(fixture), true, operation, cancellationToken).ConfigureAwait(true);
            string logs = Path.Combine(fixture.Settings.Root, "logs");
            Directory.CreateDirectory(logs);
            foreach (string path in Directory.GetFiles(Path.GetDirectoryName(specification)!))
            {
                File.Copy(path, Path.Combine(logs, Path.GetFileName(path)));
            }
            return Path.Combine(logs, Path.GetFileName(specification));
        }
        finally
        {
            await InstallationFiles.DeleteDirectoryAsync(Path.GetDirectoryName(workspace)!, Path.GetFileName(workspace), CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>実サービスを作らず、記録ファイル用の配置情報を用意します。</summary>
    /// <returns>試験専用の導入記録です。</returns>
    private static InstallationRecord Record(SetupFixture fixture) => new(Guid.NewGuid(), fixture.Settings, "fixture-service", "core.exe", "updater.exe", "client.exe", DateTimeOffset.UtcNow);
}
