using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>設定編集とPeer実体の起動を混同しないことを検証します。</summary>
public sealed class ServiceStartedTests
{
    /// <summary>同じ実体での再適用は抑止し、新しい実体では自分の起動を通知します。</summary>
    /// <returns>一度だけの起動通知の確認です。</returns>
    [Fact]
    public async Task ReapplicationDoesNotRepeatStartupButNewPeerInstanceDoes()
    {
        List<NodeEmission> emissions = [];
        NodeExecutionContext initial = new(Guid.NewGuid(), Guid.NewGuid(), "w", "r1", "startup", "a", NodeSettings.Schema("{}"), new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System, TimeZoneInfo.Utc);
        NodeSourceContext context = new(initial, () => initial, (item, _) =>
        {
            emissions.Add(item);
            return ValueTask.CompletedTask;
        }, static () => false);
        Guid firstInstance = Guid.NewGuid();
        ServiceStartedNode first = new(firstInstance, DateTimeOffset.UnixEpoch);

        _ = await first.RunAsync(context).ConfigureAwait(true);
        _ = await first.RunAsync(context).ConfigureAwait(true);
        Assert.Single(emissions);
        Assert.Equal(firstInstance, emissions[0].Result.Outputs["value"].Json.GetProperty("instanceId").GetGuid());
        Guid nextInstance = Guid.NewGuid();
        _ = await new ServiceStartedNode(nextInstance, DateTimeOffset.UtcNow).RunAsync(context).ConfigureAwait(true);
        Assert.Equal(2, emissions.Count);
        Assert.Equal(nextInstance, emissions[1].Result.Outputs["value"].Json.GetProperty("instanceId").GetGuid());
    }
}
