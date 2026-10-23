using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>現在状態の検査と、保守・予約の変更に追従する検証済みキャッシュを管理します。</summary>
internal sealed class SqliteCurrentVersion(string path, TimeProvider? timeProvider = null, SqlitePreparationCache? preparedCache = null)
{
    private SharedVersion? cached;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SqlitePreparationCache preparations = preparedCache ?? new(timeProvider);

    /// <summary>保存本文と保守世代を同じ取引で確認し、必要な場合だけ現在状態を再計算します。</summary>
    /// <returns>現在状態と未確定予約の識別子です。</returns>
    internal string Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // SQL外の破損検査には、接続に残るページキャッシュを使いません。
        using SqliteSession session = new(path, write: false, usePooling: false);
        SqliteSharedChanges.Generation generation = SqliteSharedChanges.Read(session);
        bool retained = session.Read<DurableEntry>("entry", DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, "boundary")) is not null;
        Guid[] pendingIds = preparations.SharedIds(session, cancellationToken);
        long retirementRevision = 0;
        using SqliteCommand changes = session.HistoryCommand();
        changes.CommandText = "SELECT revision FROM retention_changes WHERE id=1";
        retirementRevision = (long)changes.ExecuteScalar()!;
        SharedVersion? previous = Volatile.Read(ref cached);
        if (previous is not null && previous.Generation == generation && previous.Retained == retained && previous.RetirementRevision == retirementRevision
            && clock.GetElapsedTime(previous.CheckedAt) is { TotalSeconds: >= 0 and < 30 })
        {
            return RepositoryStateVersion.WithPending(previous.Version, retained, pendingIds);
        }

        // 一件の予約変更で、未変更の大きなワークフロー本文まで読み直しません。
        // SQL更新は行ごとの世代で即座に検出します。SQL外の破損検査の期限は、他行の変更では延長しません。
        bool reuse = previous?.Rows is not null && previous.Generation.Epoch == generation.Epoch
            && clock.GetElapsedTime(previous.CheckedAt) is { TotalSeconds: >= 0 and < 30 };
        SqliteVersionRows? known = null;
        long checkedAt = clock.GetTimestamp();
        if (reuse)
        {
            known = previous!.Rows;
            checkedAt = previous.CheckedAt;
        }
        SqliteSharedFingerprint.VerifiedRows? verified = session.SharedFingerprint(known);
        string? fingerprint = verified?.Fingerprint;
        if (fingerprint is not null)
        {
            fingerprint += retained.ToString() + retirementRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (fingerprint is not null && string.Equals(previous?.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            Volatile.Write(ref cached, previous! with { Generation = generation, CheckedAt = checkedAt });
            return RepositoryStateVersion.WithPending(previous!.Version, retained, pendingIds);
        }
        string version;
        SqliteVersionRows? rows = null;
        if (verified is not null)
        {
            (rows, version) = SqliteVersionRows.Read(session, verified.Rows, previous?.Rows, retained,
                previous?.RetirementRevision != retirementRevision || previous?.Retained != retained);
        }
        else
        {
            DurableEntry[] entries = session.List<DurableEntry>("entry", sharedOnly: true, excludeRetired: retained, cancellationToken: cancellationToken).Concat(session.List<DurableEntry>("conflict", sharedOnly: true, cancellationToken: cancellationToken)).ToArray();
            version = RepositoryStateVersion.CurrentHash(entries, retained, SqliteVersionRows.ProtectedHistory(session, entries));
        }
        Volatile.Write(ref cached, new(fingerprint, version, rows, retirementRevision, retained, generation, checkedAt));
        return RepositoryStateVersion.WithPending(version, retained, pendingIds);
    }

    /// <summary>同じ取引で確認できた変更位置と現在状態の組です。</summary>
    private sealed record SharedVersion(string? Fingerprint, string Version, SqliteVersionRows? Rows, long RetirementRevision, bool Retained,
        SqliteSharedChanges.Generation Generation, long CheckedAt);
}
