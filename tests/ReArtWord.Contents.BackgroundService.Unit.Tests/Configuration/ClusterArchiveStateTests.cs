using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>通常の保存・反映経路と同じ保存形式で、一括復元の実有効状態を確認します。</summary>
public sealed class ClusterArchiveStateTests
{
    /// <summary>保存版と有効版が異なるWF・共通設定を戻し、選択した場合だけ余分なWFを削除します。</summary>
    /// <returns>実保存・保護・共有確定・再採用の検証です。</returns>
    [Theory]
    [InlineData(EArchiveImportMode.Merge)]
    [InlineData(EArchiveImportMode.Replace)]
    public async Task OneCommitRestoresBothRevisionsAndHonorsExtraWorkflowSelection(EArchiveImportMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-archive-" + Guid.NewGuid().ToString("N"));
        JsonFileRepository repository = new(Path.Combine(root, "state.json"));
        using UdpClient port = new(new IPEndPoint(IPAddress.Loopback, 0));
        int number = ((IPEndPoint)port.Client.LocalEndPoint!).Port;
        port.Close();
        PeerCluster cluster = new(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", number, 1, true, root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore workflows = new(repository, cluster, new WorkflowCompiler(new NodeRegistry([new PassNode()])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, _) =>
            {
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
            DeactivateHandler = (id, _) =>
            {
                effective.Remove(id);
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        using ClusterConfigurationStore common = new(repository, cluster);
        ClusterOperationalSettings settings = new("UTC", default, default, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        Assert.Equal(EOutcome.Succeeded, (await common.InitializeAsync(settings, true)).Outcome);
        string originalCommon = (await common.GetAsync()).Saved!.Revision;
        await common.SaveAsync(new(Guid.NewGuid(), settings with { TimeZoneId = "Asia/Tokyo" }, originalCommon, EActivationPolicy.Explicit));
        WorkflowSaveResult first = await workflows.SaveAsync(new(Guid.NewGuid(), Definition("screen", 1), null));
        Assert.Equal(EOutcome.Succeeded, first.Activation.Outcome);
        WorkflowSaveResult draft = await workflows.SaveAsync(new(Guid.NewGuid(), Definition("screen", 2) with { Enabled = false }, first.Revision, EActivationPolicy.Explicit));
        ClusterArchiveState state = new(cluster, workflows, common);
        ClusterArchiveConfiguration archive = await state.CaptureAsync();
        Assert.Empty(await state.ValidateAsync(archive));
        await workflows.SaveAsync(new(Guid.NewGuid(), Definition("screen", 3), draft.Revision));
        await workflows.SaveAsync(new(Guid.NewGuid(), Definition("local-only", 4), null));
        ClusterOperationalState savedCommon = await common.GetAsync();
        await common.SaveAsync(new(Guid.NewGuid(), settings with { TimeZoneId = "Europe/London" }, savedCommon.Saved!.Revision));
        ClusterArchiveConfiguration before = await state.CaptureAsync();
        Guid operation = Guid.NewGuid();
        SnapshotEditProtection protection = new(repository, cluster);
        SnapshotEditLock[] locks = before.Workflows.Select(item => new SnapshotEditLock(operation, item.Saved.Definition.Id, null))
            .Append(new(operation, ClusterConfigurationStore.Id, null, true)).ToArray();
        Assert.Equal(EOutcome.Succeeded, (await protection.AcquireAsync(operation, locks)).Outcome);
        IReadOnlyList<DurableMutation> mutations = await new ClusterArchiveMutations(repository, cluster).CreateAsync(operation, before, archive, mode);
        Assert.Equal(EOutcome.Succeeded, (await cluster.ReplicateAsync(operation, mutations)).Outcome);
        await workflows.ReconcileActivationAsync();
        Assert.Equal(EOutcome.Succeeded, (await common.ReconcileAsync()).Outcome);
        ClusterArchiveConfiguration actual = await state.CaptureAsync();
        Assert.Equal(ClusterArchiveState.Fingerprint(ClusterArchiveState.Desired(before, archive, mode)), ClusterArchiveState.Fingerprint(actual));
        Assert.Equal(draft.Revision, (await workflows.GetAsync("screen"))!.Revision);
        Assert.Equal(first.Revision, effective["screen"].Revision);
        Assert.True(effective["screen"].Definition.Enabled);
        Assert.Equal(mode == EArchiveImportMode.Merge, (await workflows.GetAsync("local-only")) is not null);
        Assert.Equal("Asia/Tokyo", actual.Operational.Saved!.Settings.TimeZoneId);
        Assert.Equal("UTC", actual.Operational.Effective!.Settings.TimeZoneId);
        Assert.Equal(EOutcome.Succeeded, (await protection.ReleaseAsync(operation)).Outcome);
    }

    /// <summary>版の違いを外から確認できる定数入力です。</summary>
    /// <returns>有効な通常WFです。</returns>
    private static WorkflowDefinition Definition(string id, int value) => new(id, id, [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) })], [], Enabled: true);
}
