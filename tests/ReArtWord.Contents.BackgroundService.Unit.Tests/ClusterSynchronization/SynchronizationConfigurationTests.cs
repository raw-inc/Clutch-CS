using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>同期資源の設定を型と範囲で検証します。</summary>
public sealed class SynchronizationConfigurationTests
{
    /// <summary>UDPの4MiBとは独立した作業容量を設定できます。</summary>
    [Fact]
    public void ValidOptionsAreBoundWithoutImplicitConversion()
    {
        CommonConfiguration settings = CommonConfigurationReader.Read(Base + "\nsyncSessionLifetimeSeconds=12\nmaximumSyncSnapshotBytes=2147483648\nmaximumSyncSessions=2", Path.GetTempPath());
        Assert.Equal(TimeSpan.FromSeconds(12), settings.Cluster.SyncSessionLifetime);
        Assert.Equal(2147483648L, settings.Cluster.MaximumSyncSnapshotBytes);
        Assert.Equal(2, settings.Cluster.MaximumSyncSessions);
        Assert.Equal(4 * 1024 * 1024, settings.Cluster.MaximumMessageBytes);
    }

    /// <summary>型違い・非正数・上限超過を起動前に拒否します。</summary>
    [Theory]
    [InlineData("syncSessionLifetimeSeconds=0")]
    [InlineData("syncSessionLifetimeSeconds=3601")]
    [InlineData("maximumSyncSnapshotBytes=1024")]
    [InlineData("maximumSyncSnapshotBytes=1099511627777")]
    [InlineData("maximumSyncSessions=33")]
    [InlineData("maximumSyncSessions=\"4\"")]
    public void InvalidOptionsAreRejected(string value)
    {
        Assert.Throws<FormatException>(() => CommonConfigurationReader.Read(Base + "\n" + value, Path.GetTempPath()));
    }

    /// <summary>外部通信を開始しない設定読取用の最小TOMLです。</summary>
    private static string Base => "[cluster]\nid='test'\npeerId='test'\naddress='127.0.0.1'\nbroadcast='127.255.255.255'";
}
