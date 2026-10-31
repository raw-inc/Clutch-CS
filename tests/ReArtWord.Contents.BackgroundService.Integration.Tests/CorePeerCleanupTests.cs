using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>診断保存に失敗しても、試験用Coreの監視資源を解放します。</summary>
public sealed class CorePeerCleanupTests
{
    /// <summary>起動不能なCoreの診断を書けない場合も、破棄済みの監視を再開しません。</summary>
    /// <returns>保存失敗の通知と監視の終了確認です。</returns>
    // 抑制理由: DisposeAsync自体の失敗を検証し、finallyでも所有するPeerの回収を試みます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "破棄の例外を明示的に検証し、finallyで残った試験資源を回収します。")]
    [Fact]
    public async Task DiagnosticWriteFailureStillDisposesTheObserverAsync()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "failed-start", CorePeer.AvailablePort(), true);
        string diagnostic = Path.Combine(peer.DirectoryPath, "startup-error.txt");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(peer.DirectoryPath, "contents.toml"), "[cluster").ConfigureAwait(true);
            await Assert.ThrowsAsync<IOException>(() => peer.StartAsync()).ConfigureAwait(true);
            Directory.CreateDirectory(diagnostic);
            Exception? failure = await Record.ExceptionAsync(() => ((IAsyncDisposable)peer).DisposeAsync().AsTask()).ConfigureAwait(true);
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => peer.StartAsync()).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(diagnostic))
                {
                    Directory.Delete(diagnostic);
                }
            }
            finally
            {
                try
                {
                    await ((IAsyncDisposable)peer).DisposeAsync().ConfigureAwait(true);
                }
                catch (ObjectDisposedException)
                {
                    // 検証対象の破棄が完了済みの場合だけ、後始末の重複を許容します。
                }
            }
        }
    }
}
