using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>試験用配置を初期化できなかった場合の後始末を確認します。</summary>
public sealed class ClientFixtureCleanupTests
{
    /// <summary>未初期化の更新用配置を破棄しても、別のClientHostへ影響しません。</summary>
    /// <returns>HTTP資源の解放と、別配置の応答継続です。</returns>
    // 抑制理由: DisposeAsyncの失敗を検証し、未初期化の配置が持つHTTP資源をfinallyで回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "未初期化時の破棄を明示的に検証し、唯一の所有資源をfinallyで回収します。")]
    [Fact]
    public async Task UninitializedUpdateFixtureDoesNotTouchAnotherClientAsync()
    {
        string state = Path.Combine(Path.GetTempPath(), "clutch-client-cleanup-" + Guid.NewGuid().ToString("N"));
        ClientHostProcess other = new(state);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable otherLifetime = other.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await other.StartAsync(timeout.Token).ConfigureAwait(true);
        ClientUpdateInstallation fixture = new();
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => fixture.DisposeAsync().AsTask()).ConfigureAwait(true);
            Assert.Null(failure);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Http.GetAsync(new Uri("client/health", UriKind.Relative), timeout.Token)).ConfigureAwait(true);
            using HttpResponseMessage response = await other.SendAsync(HttpMethod.Get, "client/health", cancellationToken: timeout.Token).ConfigureAwait(true);
            Assert.True(response.IsSuccessStatusCode);
        }
        finally
        {
            fixture.Http.Dispose();
        }
    }
}
