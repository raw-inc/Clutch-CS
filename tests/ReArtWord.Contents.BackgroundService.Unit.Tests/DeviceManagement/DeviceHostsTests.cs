using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実OSのhostsに触れず、実ファイルで退避と原子的置換を確認します。</summary>
public sealed class DeviceHostsTests
{
    /// <summary>UTF-8/UTF-16のBOMと元の改行を保ち、元バイト列を退避します。</summary>
    /// <returns>隔離ファイルの反映確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesOriginalEncodingAndBackupAsync(bool unicode)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "hosts");
        Encoding encoding = unicode ? Encoding.Unicode
                                    : new UTF8Encoding(true);
        await File.WriteAllTextAsync(path, "# 日本語\r\n127.0.0.1 localhost\r\n", encoding);
        byte[] original = await File.ReadAllBytesAsync(path);
        string backups = Path.Combine(files.Root, "backup");
        DeviceHostsFile hosts = new(path, backups);
        DeviceSnapshot before = await hosts.ReadAsync();
        DeviceChangeRequest request = Change(before, "# 次の設定\n127.0.0.1 localhost\n10.0.0.2 display\n");
        Assert.Equal(EOutcome.Succeeded, (await hosts.ApplyAsync(request, before)).Outcome);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(backups, request.OperationId.ToString("N") + ".hosts")));
        byte[] next = await File.ReadAllBytesAsync(path);
        Assert.True(next.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Contains("# 次の設定\r\n", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    /// <summary>確認後に別アプリが変更した内容を上書きしません。</summary>
    /// <returns>外部変更の保持確認です。</returns>
    [Fact]
    public async Task RejectsExternalEditsBeforeReplacementAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "hosts"), backup = Path.Combine(files.Root, "backup");
        await File.WriteAllTextAsync(path, "127.0.0.1 localhost\n");
        DeviceHostsFile hosts = new(path, backup);
        DeviceSnapshot before = await hosts.ReadAsync();
        await File.WriteAllTextAsync(path, "10.0.0.3 external\n");
        DeviceChangeResult result = await hosts.ApplyAsync(Change(before, "10.0.0.4 desired\n"), before);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("10.0.0.3 external\n", await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(backup));
    }

    /// <summary>IP、別名、制御文字、長さの違反を実変更前に拒否します。</summary>
    [Theory]
    [InlineData("not-an-ip display")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1 bad/name")]
    [InlineData("127.0.0.1 host\0")]
    [InlineData("127.1 host")]
    [InlineData("127.0.0.1 ---")]
    public void RejectsInvalidHosts(string text) => Assert.Throws<ArgumentException>(() => DeviceHostsRules.Validate(text));

    /// <summary>全文置換の固定要求を作ります。</summary>
    /// <returns>副作用前に比較する版を持った要求です。</returns>
    private static DeviceChangeRequest Change(DeviceSnapshot before, string content) => new(Guid.NewGuid(), "host", Guid.NewGuid(), EDeviceAction.Hosts,
        new(EDeviceArea.Hosts), before.Revision, DeviceSnapshots.Create(new DeviceHostsChange(content)).Data);
}
