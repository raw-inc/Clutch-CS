using System.Net;
using System.Net.NetworkInformation;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>実リソース観測をCore実体と起動時の選択NICへ結び付けます。</summary>
public sealed class ResourceSampleIdentityTests
{
    /// <summary>同じ観測と保存履歴に実体と選択NICを含め、非存在IPへ代替しません。</summary>
    /// <returns>OS実観測と保持されたメタデータの確認です。</returns>
    [Fact]
    public async Task CaptureCarriesInstanceAndExplicitInterfaceIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-resource-identity-" + Guid.NewGuid().ToString("N"));
        SqliteRepository repository = new(Path.Combine(root, "state.db"));
        Guid instance = Guid.NewGuid();
        using ResourceService resources = new(repository, "peer", new(), instance, "127.0.0.1");
        ResourceSample sample = await resources.CaptureAsync();
        string nic = Assert.Single(NetworkInterface.GetAllNetworkInterfaces(), network => network.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        Assert.Equal(instance, sample.InstanceId);
        Assert.Equal(nic, sample.SelectedInterfaceId);
        Assert.Contains(sample.Metrics, metric => string.Equals(metric.Target, nic, StringComparison.Ordinal));
        Assert.Equal(instance, Assert.Single(await resources.HistoryAsync()).InstanceId);
        using ResourceService unknown = new(repository, "other", new(), Guid.NewGuid(), "192.0.2.254");
        Assert.Null((await unknown.CaptureAsync()).SelectedInterfaceId);
    }
}
