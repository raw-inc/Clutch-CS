using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Secrets;

/// <summary>秘密の管理一覧と保存確認を検証します。</summary>
public sealed class SecretStoreTests
{
    /// <summary>共有経路なしの保存を拒否し、再起動後も名前だけを列挙します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task SharedWriterRequiredAndNamesOnly()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "secret.db");
        SqliteRepository repository = new(path);
        SecretStore store = new(repository);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SetAsync("api", "secret-value").ConfigureAwait(false));
        store.Replicator = repository.ApplyBatchAsync;
        await store.SetAsync("api", "secret-value");
        SecretStore restarted = new(new SqliteRepository(path));
        Assert.Equal("secret-value", await restarted.GetAsync("api"));
        Assert.Equal("api", Assert.Single(await restarted.ListNamesAsync()));
    }
}
