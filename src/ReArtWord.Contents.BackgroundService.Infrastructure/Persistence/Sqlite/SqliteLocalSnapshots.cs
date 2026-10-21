using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>ローカル診断の対象行だけを一取引で置換し、耐久性を維持してflush回数を減らします。</summary>
internal static class SqliteLocalSnapshots
{
    /// <summary>ローカル診断JSONを、一つの取引で直接保存します。</summary>
    /// <returns>永続確定の結果です。</returns>
    internal static ValueTask<EOutcome> WriteJsonAsync(string path, string scope, string key, Action<System.Text.Json.Utf8JsonWriter> writeValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        session.WriteJsonSnapshot(scope, key, writeValue, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return ValueTask.FromResult(EOutcome.Succeeded);
    }

    /// <summary>不正な項目・取消は全項目の確定前に拒否します。</summary>
    /// <returns>全行の確定結果です。</returns>
    internal static ValueTask<EOutcome> WriteAsync(string path, IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken = default)
    {
        RepositoryDocument document = LocalSnapshotBatches.Own(snapshots, cancellationToken);
        using SqliteSession session = new(path);
        session.Save(document);
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return ValueTask.FromResult(EOutcome.Succeeded);
    }
}
