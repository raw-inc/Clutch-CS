using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>一回だけ追記する試験の観測が、出力前のファイルを占有しないことを確認します。</summary>
public sealed class FileObservationTests
{
    /// <summary>未作成と空の状態は未完了とし、排他書込みの終了後に本文を読みます。</summary>
    /// <returns>作成前から追記完了までの確認です。</returns>
    [Fact]
    public async Task ReadsOnlyAfterFirstAppendAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "clutch-observation-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.Null(await FileObservation.ReadFirstAppendAsync(path).ConfigureAwait(true));
            await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(true);
            Assert.Null(await FileObservation.ReadFirstAppendAsync(path).ConfigureAwait(true));
            using (FileStream writer = new(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                Assert.Null(await FileObservation.ReadFirstAppendAsync(path).ConfigureAwait(true));
                writer.Write("tick\n"u8);
                await writer.FlushAsync().ConfigureAwait(true);
                if (OperatingSystem.IsWindows())
                {
                    Assert.Null(await FileObservation.ReadFirstAppendAsync(path).ConfigureAwait(true));
                }
            }
            Assert.Equal("tick\n", await FileObservation.ReadFirstAppendAsync(path).ConfigureAwait(true));
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync().ConfigureAwait(true);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileObservation.ReadFirstAppendAsync(path, cancelled.Token)).ConfigureAwait(true);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
