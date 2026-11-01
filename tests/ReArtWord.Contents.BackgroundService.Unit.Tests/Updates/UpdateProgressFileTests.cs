using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>実ファイルの共有・アクセス拒否と、進捗保存の中止境界を検証します。</summary>
[Collection("TimingSensitive")]
public sealed class UpdateProgressFileTests : IDisposable
{
    private readonly string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "contents-progress-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>読み始めの共有競合を待ち、完全なJSONだけを返します。</summary>
    /// <returns>進捗ポーリング側の一時エラーの回帰確認です。</returns>
    [WindowsFact]
    public async Task ReaderWaitsForTransientFileSharingAsync()
    {
        string path = Path.Combine(directory, "progress.json");
        await UpdateProgressFile.WriteAsync(path, "committed").ConfigureAwait(true);
        Task<string?> reading = Task.FromResult<string?>(null);
        try
        {
            using FileStream owner = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            reading = UpdateProgressFile.ReadAsync<string>(path);
            await Task.Delay(150).ConfigureAwait(true);
            Assert.False(reading.IsCompleted);
        }
        finally
        {
            Assert.Equal("committed", await reading.ConfigureAwait(true));
        }
    }

    /// <summary>不正なJSONを一時競合や成功として扱いません。</summary>
    /// <returns>永続記録の破損を通知する確認です。</returns>
    [Fact]
    public async Task ReaderRejectsCorruptedProgressAsync()
    {
        string path = Path.Combine(directory, "progress.json");
        await File.WriteAllTextAsync(path, "{broken").ConfigureAwait(true);
        await Assert.ThrowsAsync<JsonException>(() => UpdateProgressFile.ReadAsync<string>(path)).ConfigureAwait(true);
    }

    /// <summary>置換前から開いている読取りは旧本文、新しい読取りは確定した新本文を取得します。</summary>
    /// <returns>読取り中も本文を途中状態にしない確認です。</returns>
    [Fact]
    public async Task ReplacementPreservesReadersOfPreviousProgressAsync()
    {
        string path = Path.Combine(directory, "progress.json");
        await UpdateProgressFile.WriteAsync(path, "previous").ConfigureAwait(true);
        using FileStream previous = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        string next = new('x', 65536);
        await UpdateProgressFile.WriteAsync(path, next).ConfigureAwait(true);
        Assert.Equal("previous", await JsonSerializer.DeserializeAsync<string>(previous).ConfigureAwait(true));
        Assert.Equal(next, JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path).ConfigureAwait(true)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>Windowsの実共有違反またはアクセス拒否が解消すると、同じ準備本文の保存だけを完了します。</summary>
    /// <returns>一時競合の間は旧記録を保ち、解消後に新記録へ置換する確認です。</returns>
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientReplacementFailureRetainsCommittedProgressAsync(bool readOnly)
    {
        string path = Path.Combine(directory, "progress.json");
        await UpdateProgressFile.WriteAsync(path, "previous").ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task saving = Task.CompletedTask;
        try
        {
            using FileStream? reader = readOnly ? null
                                                : new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (readOnly)
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            saving = UpdateProgressFile.WriteAsync(path, "next", timeout.Token);
            await Task.Delay(150, timeout.Token).ConfigureAwait(true);
            Assert.False(saving.IsCompleted);
            Assert.Equal("previous", JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true)));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            await saving.ConfigureAwait(true);
        }
        Assert.Equal("next", JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true)));
    }

    /// <summary>恒久的な権限問題を成功扱いせず、有限の待機後に原因と旧記録を残します。</summary>
    /// <returns>アクセス拒否の上限と原子的保存の確認です。</returns>
    [WindowsFact]
    public async Task PersistentAccessDenialStopsWithoutReplacingCommittedProgressAsync()
    {
        string path = Path.Combine(directory, "progress.json");
        await UpdateProgressFile.WriteAsync(path, "previous").ConfigureAwait(true);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            UnauthorizedAccessException error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => UpdateProgressFile.WriteAsync(path, "next")).ConfigureAwait(true);
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            Assert.Equal("previous", JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path).ConfigureAwait(true)));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>共有競合を待つ間の中止は保存を進めず、既存記録を残します。</summary>
    /// <returns>中止後に競合が解消しても再開しない確認です。</returns>
    [WindowsFact]
    public async Task CancellationStopsReplacementRetryAsync()
    {
        string path = Path.Combine(directory, "progress.json");
        await UpdateProgressFile.WriteAsync(path, "previous").ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        using (FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Task saving = UpdateProgressFile.WriteAsync(path, "next", cancellation.Token);
            try
            {
                await Task.Delay(100).ConfigureAwait(true);
                Assert.False(saving.IsCompleted);
            }
            finally
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
                await saving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving).ConfigureAwait(true);
        }
        Assert.Equal("previous", JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path).ConfigureAwait(true)));
    }

    /// <summary>保存先不在は競合扱いせず、その場で明確なエラーにします。</summary>
    /// <returns>OSエラーの分類を広げすぎない確認です。</returns>
    [Fact]
    public async Task MissingDirectoryDoesNotCreateAProgressFileAsync()
    {
        string path = Path.Combine(directory, "missing", "progress.json");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => UpdateProgressFile.WriteAsync(path, "next")).ConfigureAwait(true);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    /// <summary>この試験だけの一意な保存領域を解放します。</summary>
    public void Dispose() => Directory.Delete(directory, true);
}
