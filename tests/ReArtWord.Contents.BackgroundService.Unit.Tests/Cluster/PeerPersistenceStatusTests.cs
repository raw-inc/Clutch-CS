using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>保存状態の一時通知が既知集合の永続履歴を増やさないことを検証します。</summary>
public sealed class PeerPersistenceStatusTests
{
    /// <summary>両方向で変わる保存件数を通知しても既知集合の版を更新しません。</summary>
    /// <returns>通知・変更番号・実SQLite保存版の検証です。</returns>
    [Fact]
    public async Task CountsChangeWithoutChangingKnownRevisionOrPersistentRecord()
    {
        using MediaTestFiles files = new();
        ClusterOptions options = new("counts", "a", "a", "127.0.0.1", "127.255.255.255", 59001, 1, true, files.Root);
        PeerMembership first = new(options, Guid.NewGuid(), TimeProvider.System);
        PeerMembership second = new(options with { PeerId = "b", HostId = "b" }, Guid.NewGuid(), TimeProvider.System);
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        using PeerKnownMembers known = new(options, repository, first);
        Assert.True(first.Observe(second.CreateAnnouncement("digest"), "127.0.0.1"));
        Assert.True(second.Observe(first.CreateAnnouncement("digest"), "127.0.0.1"));
        Assert.True(await known.FlushAsync().ConfigureAwait(true));
        long firstRevision = first.KnownRevision;
        long secondRevision = second.KnownRevision;
        string version = (await repository.ReadAsync("peer:a:membership", "known").ConfigureAwait(true))!.Version;
        for (int index = 1; index <= 20; index++)
        {
            first.SetLocalPersistenceStatus(new(index, index + 1, index + 2));
            second.SetLocalPersistenceStatus(new(index + 3, index + 4, index + 5));
            Assert.True(first.Observe(second.CreateAnnouncement("digest"), "127.0.0.1"));
            Assert.True(second.Observe(first.CreateAnnouncement("digest"), "127.0.0.1"));
            Assert.True(await known.FlushAsync().ConfigureAwait(true));
            Assert.Equal(firstRevision, first.KnownRevision);
            Assert.Equal(secondRevision, second.KnownRevision);
            Assert.Equal(version, (await repository.ReadAsync("peer:a:membership", "known").ConfigureAwait(true))!.Version);
        }
        Assert.Equal(23, first.Snapshot().Single(peer => string.Equals(peer.PeerId, "b", StringComparison.Ordinal)).PersistenceStatus!.UnsavedActive);
        Assert.Equal(20, second.Snapshot().Single(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal)).PersistenceStatus!.UnsavedActive);
    }
}
