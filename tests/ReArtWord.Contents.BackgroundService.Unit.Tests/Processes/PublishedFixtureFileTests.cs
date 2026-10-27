using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>公開直後にリネーム用ハンドルが残っていても、完成した試験記録を読み取ります。</summary>
public sealed class PublishedFixtureFileTests
{
    /// <summary>一時ファイルを除外し、公開後はPID・開始時刻・末尾の空引数を保ちます。</summary>
    [Fact]
    public void StartRecordCanBeReadWhileRenameHandleIsOpen()
    {
        using ApplicationProcessFixture fixture = new();
        string path = Path.Combine(fixture.Root, "fixture.started");
        string[] record = [int.MaxValue.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), "日本語 space", "quoted\"value", string.Empty];
        File.WriteAllLines(path + ".tmp", record);

        // DeleteOnCloseはWindowsでDELETEアクセスを取得し、リネーム中と同じ共有条件を再現します。
        using FileStream rename = new(path + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        Assert.Empty(fixture.Starts());
        File.Move(path + ".tmp", path);
        Assert.Equal(record, Assert.Single(fixture.Starts()));
    }

    /// <summary>ウィンドウ情報のJSONも、公開用ハンドルの解放前に読み取れます。</summary>
    /// <returns>公開された内容の確認です。</returns>
    [Fact]
    public async Task WindowRecordCanBeReadWhileRenameHandleIsOpenAsync()
    {
        using ApplicationProcessFixture fixture = new();
        string path = Path.Combine(fixture.Root, "fixture.window.json");
        await File.WriteAllTextAsync(path + ".tmp", "{\"visible\":true,\"arguments\":[\"日本語 space\",\"\"]}");
        using FileStream rename = new(path + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        File.Move(path + ".tmp", path);
        using JsonDocument record = JsonDocument.Parse(await PublishedFixtureFile.ReadAllTextAsync(path));
        Assert.True(record.RootElement.GetProperty("visible").GetBoolean());
        Assert.Equal("日本語 space", record.RootElement.GetProperty("arguments")[0].GetString());
        Assert.Equal(string.Empty, record.RootElement.GetProperty("arguments")[1].GetString());
    }
}
