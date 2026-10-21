using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>稼働中の保存先と接続の所有期間をまとめ、Core終了時に解放します。</summary>
public sealed class RepositoryRuntime : IDisposable
{
    private readonly SqliteConnectionLifetime connections;

    /// <summary>初期化が完了した保存先と、その接続の所有権を受け取ります。</summary>
    internal RepositoryRuntime(IDurableRepository repository, SqliteConnectionLifetime connections)
    {
        Repository = repository;
        this.connections = connections;
    }

    /// <summary>所有期間中に使用する保存先です。</summary>
    public IDurableRepository Repository
    {
        get;
    }

    /// <summary>保存を使用する処理の終了後に、保持した接続を閉じます。</summary>
    public void Dispose() => connections.Dispose();
}
