using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>利用者指定の優先度規則と境界値を検証します。</summary>
public sealed class InstallationPolicyTests
{
    /// <summary>単独参加の減算を32bit下限で安全に処理します。</summary>
    [Theory]
    [InlineData(1000, 990)]
    [InlineData(0, -10)]
    [InlineData(int.MinValue + 10, int.MinValue)]
    [InlineData(int.MinValue + 9, int.MinValue + 8)]
    [InlineData(int.MinValue + 1, int.MinValue)]
    [InlineData(int.MinValue, int.MinValue)]
    public void SinglePeerPriority(int existing, int expected) => Assert.Equal(expected, InstallationPolicy.AutomaticPriority([Peer(existing)]));

    /// <summary>複数参加では最小値、新規では1000を使います。</summary>
    [Fact]
    public void MultipleAndNewClusterPriority()
    {
        Assert.Equal(1000, InstallationPolicy.AutomaticPriority([]));
        Assert.Equal(-20, InstallationPolicy.AutomaticPriority([Peer(500), Peer(-20), Peer(40)]));
    }

    /// <summary>実体を区別できる観測を作ります。</summary>
    /// <returns>試験用Peerです。</returns>
    private static PeerInfo Peer(int priority) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, priority, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
