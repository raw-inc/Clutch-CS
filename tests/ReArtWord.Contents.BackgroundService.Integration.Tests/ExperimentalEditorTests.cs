using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>共有草稿・共通の反映方針・処理中の読取りを実Coreで確認します。</summary>
public sealed class ExperimentalEditorTests
{
    /// <summary>手動反映を既定に保ち、共有設定の有効化・再起動・解除に追従します。</summary>
    /// <returns>二つの実プロセスと実通信による確認です。</returns>
    [Fact]
    public async Task SharedDraftsRemainManualUntilClusterPolicyEnablesLiveApplyAsync()
    {
        using ControlledHttpEndpoint endpoint = new();
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
        }).ConfigureAwait(true);
        WorkflowDefinition definition = new("live-editor", "original", [new("hold", "http.request", JsonSerializer.SerializeToElement(new
        {
            interfaceId = first.InterfaceId, localAddress = "127.0.0.1", url = endpoint.Url, method = "GET", authentication = "none", timeoutMs = 0,
        }), Target: new(EExecutionScope.One, ["a"]))], [], Enabled: false);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        await EditAsync(second, "shared draft").ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<DraftView>("collaboration.draft", definition.Id, token).ConfigureAwait(true))
            .Definition.Name is "shared draft").ConfigureAwait(true);
        await Task.Delay(2200).ConfigureAwait(true);
        Assert.Equal("original", (await ReadAsync(first).ConfigureAwait(true)).GetProperty("saved").GetProperty("definition").GetProperty("name").GetString());
        await SetPolicyAsync(first, true).ConfigureAwait(true);
        await WaitSavedAsync(first, "shared draft").ConfigureAwait(true);
        await WaitSavedAsync(second, "shared draft").ConfigureAwait(true);
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()
            && (await second.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective?.Settings.LiveWorkflowUpdates == true).ConfigureAwait(true);
        await EditAsync(second, "after restart").ConfigureAwait(true);
        await WaitSavedAsync(first, "after restart").ConfigureAwait(true);
        await SetPolicyAsync(first, false).ConfigureAwait(true);
        await EditAsync(second, "manual again").ConfigureAwait(true);
        await Task.Delay(2200).ConfigureAwait(true);
        JsonElement current = await ReadAsync(first).ConfigureAwait(true);
        Assert.Equal("after restart", current.GetProperty("saved").GetProperty("definition").GetProperty("name").GetString());
        Guid operation = Guid.NewGuid();
        using CancellationTokenSource observation = new(TimeSpan.FromSeconds(30));
        await first.PostAsync<JsonElement>("runs.start", new WorkflowRunRequest(operation, definition.Id, current.GetProperty("effectiveRevision").GetString(), "hold"), observation.Token).ConfigureAwait(true);
        Assert.Empty(await endpoint.AcceptAsync(observation.Token).ConfigureAwait(true));
        await CorePeer.WaitAsync(async token => (await first.GetAsync<WorkflowNodeActivity>("workflows.activity", definition.Id, token).ConfigureAwait(true)).Nodes.Any(node => string.Equals(node.NodeId, "hold", StringComparison.Ordinal) && node.CompletedAt is null), observation.Token).ConfigureAwait(true);
        Assert.Empty((await first.GetAsync<WorkflowNodeActivity>("workflows.activity", "different-flow", observation.Token).ConfigureAwait(true)).Nodes);
        await endpoint.ReplyAsync("completed", observation.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => !(await first.GetAsync<JsonElement>("runs.read", operation, token).ConfigureAwait(true)).GetProperty("running").GetBoolean(), observation.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await first.GetAsync<WorkflowRunStatus>("runs.read", operation, observation.Token).ConfigureAwait(true)).Outcome);
        Assert.Empty((await first.GetAsync<WorkflowNodeActivity>("workflows.activity", definition.Id, observation.Token).ConfigureAwait(true)).Nodes);
    }

    /// <summary>一つの共通設定だけを通常の保存・反映経路で切り替えます。</summary>
    /// <returns>共通方針の確定です。</returns>
    private static async Task SetPolicyAsync(CorePeer peer, bool enabled)
    {
        ClusterOperationalState state = await peer.GetAsync<ClusterOperationalState>("configuration.read").ConfigureAwait(true);
        ClusterOperationalSaveResult result = await peer.PostAsync<ClusterOperationalSaveResult>("configuration.save",
            new ClusterOperationalSaveRequest(Guid.NewGuid(), state.Saved!.Settings with { LiveWorkflowUpdates = enabled }, state.Saved.Revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Activation.Outcome);
    }

    /// <summary>保存APIを使わないブラウザーの編集を再現します。</summary>
    /// <returns>草稿だけの共有です。</returns>
    private static async Task EditAsync(CorePeer peer, string name)
    {
        DraftView view = await peer.GetAsync<DraftView>("collaboration.draft", "live-editor").ConfigureAwait(true);
        DraftChangeRequest change = new(Guid.NewGuid(), view.WorkflowId, Guid.NewGuid(), view.BaseRevision, view.SessionVersion,
            view.Definition, view.Definition with { Name = name }, new Dictionary<string, EEditResolution>());
        DraftView accepted = await peer.NotifyAsync<DraftView>("collaboration.changes", change).ConfigureAwait(true);
        Assert.Empty(accepted.Conflicts);
    }

    /// <summary>保存版と有効版の両方を待ちます。</summary>
    /// <returns>保存内容と実有効版の一致です。</returns>
    private static Task WaitSavedAsync(CorePeer peer, string name) => CorePeer.WaitAsync(async token =>
    {
        JsonElement current = await peer.GetAsync<JsonElement>("workflows.read", "live-editor", token).ConfigureAwait(true);
        return string.Equals(current.GetProperty("saved").GetProperty("definition").GetProperty("name").GetString(), name, StringComparison.Ordinal)
            && string.Equals(current.GetProperty("saved").GetProperty("revision").GetString(), current.GetProperty("effectiveRevision").GetString(), StringComparison.Ordinal);
    });

    /// <summary>対象の実保存を読みます。</summary>
    /// <returns>保存と実有効版です。</returns>
    private static Task<JsonElement> ReadAsync(CorePeer peer) => peer.GetAsync<JsonElement>("workflows.read", "live-editor");
}
