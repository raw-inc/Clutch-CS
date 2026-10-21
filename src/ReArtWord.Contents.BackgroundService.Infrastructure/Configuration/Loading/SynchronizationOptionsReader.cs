using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using Tomlyn.Model;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>同期作業の容量・同時数・固定期限をTOMLから明示設定します。</summary>
internal static class SynchronizationOptionsReader
{
    /// <summary>UDP一メッセージ上限とは独立した作業条件を適用します。</summary>
    /// <returns>型と範囲を検証した同期設定です。</returns>
    internal static ClusterOptions Apply(ClusterOptions options, TomlTable table) => options with
    {
        SyncSessionLifetime = TimeSpan.FromSeconds(Read(table, "syncSessionLifetimeSeconds", 300, 1, 3600)),
        MaximumSyncSnapshotBytes = Read(table, "maximumSyncSnapshotBytes", 1024L * 1024 * 1024, 65536, 1024L * 1024 * 1024 * 1024),
        MaximumSyncSessions = checked((int)Read(table, "maximumSyncSessions", 4, 1, 32)),
    };

    /// <summary>整数以外や範囲外を暗黙変換しません。</summary>
    /// <returns>検証した整数です。</returns>
    private static long Read(TomlTable table, string name, long fallback, long minimum, long maximum)
    {
        if (!table.TryGetValue(name, out object? raw))
        {
            return fallback;
        }
        if (raw is not long value || value < minimum || maximum < value)
        {
            throw new FormatException("cluster." + name + "の整数範囲が不正です。");
        }
        return value;
    }
}
