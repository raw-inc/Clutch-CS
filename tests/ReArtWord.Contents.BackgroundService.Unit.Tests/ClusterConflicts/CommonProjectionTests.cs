using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterConflicts;

/// <summary>保存候補の競合と、実行中に利用する共通設定の部分投影を分けます。</summary>
public sealed class CommonProjectionTests
{
    /// <summary>旧有効版は保存だけの競合で止めず、有効版の競合は非依存項目だけ導出します。</summary>
    /// <returns>二つの実Peerで共通祖先と候補を照合した結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedEffectiveAndSafeProjectionHaveDifferentAdmission(bool activate)
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers["a"].GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        using ClusterConfigurationStore first = new(fixture.Repositories["a"], fixture.Peers["a"]);
        ClusterOperationalSettings initial = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        Assert.Equal(EOutcome.Succeeded, (await first.InitializeAsync(initial, true)).Outcome);
        await fixture.StopAsync("b");
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int partition = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        await fixture.StartAsync("b", networkPort: partition);
        using ClusterConfigurationStore second = new(fixture.Repositories["b"], fixture.Peers["b"]);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Ready) == 1));
        await second.InitializeAsync(initial, false);
        EActivationPolicy policy = EActivationPolicy.Explicit;
        if (activate)
        {
            policy = EActivationPolicy.Auto;
        }
        string baseline = first.Capture()!.Revision;
        Assert.Equal(EOutcome.Succeeded, (await first.SaveAsync(new(Guid.NewGuid(), initial with { TimeZoneId = "Asia/Tokyo" }, baseline, policy))).Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await second.SaveAsync(new(Guid.NewGuid(), initial with { TimeZoneId = "Asia/Shanghai", FileConflictPolicy = EFileConflictPolicy.Wait }, baseline, policy))).Shared.Outcome);
        await fixture.StopAsync("b");
        await fixture.StartAsync("b");
        using ClusterConfigurationStore returned = new(fixture.Repositories["b"], fixture.Peers["b"]);
        string root = Path.GetTempPath();
        NodeRegistry registry = new([new ConditionNode(), .. FileSystemNodeRegistration.Create(new FileSystemOptions([root]))]);
        using ClusterConflictService conflicts = new(fixture.Repositories["a"], fixture.Peers["a"], new WorkflowStore(fixture.Repositories["a"], fixture.Peers["a"], new WorkflowCompiler(registry)), first, registry);
        using ClusterConflictService remote = new(fixture.Repositories["b"], fixture.Peers["b"], new WorkflowStore(fixture.Repositories["b"], fixture.Peers["b"], new WorkflowCompiler(registry)), returned, registry);
        fixture.Peers["a"].SetConflictReadinessCheck(conflicts.PrepareAsync);
        fixture.Peers["b"].SetConflictReadinessCheck(remote.PrepareAsync);
        await ManualSafetyFixture.WaitAsync(() => fixture.Peers.Values.All(peer => peer.GetPeers().Count(item => item.Status == EPeerStatus.Degraded) == 2));
        await conflicts.RefreshAsync();
        Assert.Equal(ELocalPreparationState.Prepared, (await first.PrepareLocalAsync()).State);
        ClusterOperationalSnapshot effective = first.Capture()!;
        NodeDefinition time = new("time", "control.condition", JsonSerializer.SerializeToElement(new { operation = "timeWindow" }));
        NodeDefinition file = new("file", "file.read", JsonSerializer.SerializeToElement(new { path = "test" }));
        Assert.Null(conflicts.GetExecutionProblem("independent", file));
        if (activate)
        {
            Assert.Equal(EFileConflictPolicy.Wait, effective.Settings.FileConflictPolicy);
            Assert.Equal("UTC", effective.Settings.TimeZoneId);
            Assert.Equal("TimeZoneId", Assert.Single(effective.UnresolvedFields!));
            Assert.Equal(2, effective.DerivedFromVersions!.Count);
            Assert.NotEqual(baseline, effective.Revision);
            Assert.Equal(ContractJson.Hash(effective.Settings), effective.Revision);
            Assert.NotNull(conflicts.GetExecutionProblem("independent", time));
        }
        else
        {
            Assert.Equal(baseline, effective.Revision);
            Assert.Null(conflicts.GetExecutionProblem("independent", time));
        }
    }
}
