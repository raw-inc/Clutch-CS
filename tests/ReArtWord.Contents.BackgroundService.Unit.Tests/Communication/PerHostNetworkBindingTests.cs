using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>PC別通信設定の選択と、他PCへの暗黙代替をしない境界を検証します。</summary>
public sealed class PerHostNetworkBindingTests
{
    /// <summary>同じ定義でも実行HostのNICだけを選び、共通指定や他Hostへ戻しません。</summary>
    [Fact]
    public void LocalBindingUsesExactHostAndRetainsLegacyMode()
    {
        NetworkInterface network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback)));
        HostNetworkBinding[] assignments = [new("host-a", network.Id, "127.0.0.1"), new("host-b", "missing-adapter", "127.0.0.1")];
        JsonElement settings = JsonSerializer.SerializeToElement(new { networkBindingMode = "perHost", networkBindings = assignments, interfaceId = "ignored", localAddress = "192.0.2.1" }, ContractJson.Options);
        LocalNetworkBinding selected = new(settings, "host-a");
        Assert.Equal(network.Id, selected.InterfaceId);
        Assert.Equal(IPAddress.Loopback, selected.Resolve());
        Assert.Throws<SocketException>(() => new LocalNetworkBinding(settings, "host-b").Resolve());
        Assert.Throws<ArgumentException>(() => new LocalNetworkBinding(settings, "unassigned"));
        Assert.ThrowsAny<ArgumentException>(() => new LocalNetworkBinding(settings, null));
        JsonElement legacy = JsonSerializer.SerializeToElement(new { interfaceId = network.Id, localAddress = "127.0.0.1", networkBindings = assignments }, ContractJson.Options);
        Assert.Equal(IPAddress.Loopback, new LocalNetworkBinding(legacy, "unassigned").Resolve());
    }

    /// <summary>PC別設定の重複、空値、全NICアドレスを保存時に拒否します。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"hostId\":\"a\",\"interfaceId\":\"nic\",\"localAddress\":\"0.0.0.0\"}]")]
    [InlineData("[{\"hostId\":\"a\",\"interfaceId\":\"nic\",\"localAddress\":\"::1\"}]")]
    [InlineData("[{\"hostId\":\"a\",\"interfaceId\":\"\",\"localAddress\":\"127.0.0.1\"}]")]
    [InlineData("[{\"hostId\":\"a\",\"interfaceId\":\"nic\",\"localAddress\":\"127.0.0.1\"},{\"hostId\":\"a\",\"interfaceId\":\"other\",\"localAddress\":\"127.0.0.1\"}]")]
    public void InvalidAssignmentsAreRejectedBeforeExecution(string assignments)
    {
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("{\"networkBindingMode\":\"perHost\",\"networkBindings\":" + assignments + ",\"localPort\":0}");
        Assert.NotEmpty(SocketNodeTests.Find("http.listen").Validate(new("listen", "http.listen", settings)));
    }

    /// <summary>未割り当てPCでは待受を開始せず、保存された共通NICも使いません。</summary>
    /// <returns>複数通信方式の開始前の拒否です。</returns>
    [Theory]
    [InlineData("http.listen")]
    [InlineData("osc.udp.receive")]
    [InlineData("udp.receive")]
    [InlineData("artnet.receive")]
    [InlineData("tcp.listen")]
    [InlineData("osc.tcp.listen")]
    [InlineData("websocket.listen")]
    public async Task UnassignedHostDoesNotStartListenerAsync(string type)
    {
        JsonElement settings = SocketNodeTests.Settings(new { networkBindingMode = "perHost", networkBindings = new[] { new { hostId = "other", interfaceId = "nic", localAddress = "127.0.0.1" } } });
        NodeExecutionContext initial = SocketNodeTests.Context(settings) with { HostId = "unassigned" };
        List<NodeSourceStatus> statuses = [];
        NodeSourceContext source = new(initial, () => initial, (_, _) => ValueTask.CompletedTask, () => false, (status, _) =>
        {
            statuses.Add(status);
            return ValueTask.CompletedTask;
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        NodeResult result = await ((ISourceNode)SocketNodeTests.Find(type)).RunAsync(source, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("network.binding.missing", result.ReasonCode);
        Assert.Empty(statuses);
    }
}
