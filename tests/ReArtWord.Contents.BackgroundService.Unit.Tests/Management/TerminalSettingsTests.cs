using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Terminal;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>接続先Coreの恒久方針が再起動と複数クライアント間で一貫することを確認します。</summary>
public sealed class TerminalSettingsTests
{
    /// <summary>従来方式の既定、再起動後の保持、他Coreとの分離、古い画面からの競合を検査します。</summary>
    /// <returns>保存境界の検証です。</returns>
    [Fact]
    public async Task SettingsPersistPerCoreAndRejectAStaleClientAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "settings", "terminal.json");
        TerminalSettingsStore first = new(new JsonFileRepository(path), "peer-a");
        TerminalSettings original = await first.ReadAsync();
        Assert.False(original.StrictUserToken);
        TerminalSettings enabled = await first.SaveAsync(original with { StrictUserToken = true });
        TerminalSettingsStore restarted = new(new JsonFileRepository(path), "peer-a");
        Assert.Equal(enabled, await restarted.ReadAsync());
        TerminalSettingsStore other = new(new JsonFileRepository(path), "peer-b");
        Assert.False((await other.ReadAsync()).StrictUserToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SaveAsync(original));
        Assert.Equal(enabled, await restarted.ReadAsync());
        TerminalSettings disabled = await restarted.SaveAsync(enabled with { StrictUserToken = false });
        Assert.NotEqual(enabled.Version, disabled.Version);
        Assert.Equal(disabled, await first.ReadAsync());
        Assert.Empty((await new JsonFileRepository(path).ExportAsync()).Entries);
    }
}
