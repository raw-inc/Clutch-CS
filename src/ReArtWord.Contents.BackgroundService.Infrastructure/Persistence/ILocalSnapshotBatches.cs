using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>同じ媒体の診断ヘッダーと詳細を、一つの永続確定で置換します。</summary>
public interface ILocalSnapshotBatches
{
    /// <summary>全項目を検証してから書き込み、途中の状態を公開しません。</summary>
    /// <returns>一括保存の確認結果です。</returns>
    public ValueTask<EOutcome> WriteSnapshotsAsync(IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken = default);
}

/// <summary>所有値・範囲・重複の検証を媒体間で揃えます。</summary>
internal static class LocalSnapshotBatches
{
    /// <summary>取引へ入る前に全入力を検証し、値の所有権を切り離します。</summary>
    /// <returns>履歴や予約を含まないローカル置換文書です。</returns>
    internal static RepositoryDocument Own(IReadOnlyList<LocalSnapshotWrite> snapshots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count is < 1 or > 512)
        {
            throw new ArgumentException("診断snapshotの件数は1から512件です。", nameof(snapshots));
        }
        RepositoryDocument result = new();
        foreach (LocalSnapshotWrite item in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Value);
            DurableRepository.ValidateLocalSnapshotScope(item.Scope);
            string address = DurableRepository.GetAddress(item.Scope, item.Key);
            DurableEntry entry = new(item.Scope, item.Key, Guid.NewGuid().ToString("N"), item.Value.Copy());
            if (!result.Entries.TryAdd(address, entry))
            {
                throw new ArgumentException("診断snapshotの保存先が重複しています。", nameof(snapshots));
            }
        }
        return result;
    }

    /// <summary>文書媒体でも、複製・操作履歴を作らず一度だけ保存します。</summary>
    /// <returns>全置換の確認結果です。</returns>
    internal static EOutcome Apply(IReadOnlyList<LocalSnapshotWrite> snapshots, Func<Func<RepositoryDocument, EOutcome>, EOutcome> access, CancellationToken cancellationToken)
    {
        RepositoryDocument owned = Own(snapshots, cancellationToken);
        return access(document =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach ((string key, DurableEntry value) in owned.Entries)
            {
                document.Entries[key] = value;
            }
            return EOutcome.Succeeded;
        });
    }
}
