using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core間の削除同期と、再参加・再起動で削除印を維持する確認です。</summary>
public sealed class WorkflowDeletionTests
{
    /// <summary>同じ保存版だけを削除し、別Peerの一覧と再起動へ反映します。</summary>
    /// <returns>実UDP通信と永続化の確認です。</returns>
    [Fact]
    public async Task DeletionReplicatesAndSurvivesPeerRestart()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        WorkflowDefinition definition = new("delete-demo", "delete demo", [], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<WorkflowListPage>("workflows.list", token).ConfigureAwait(true)).Entries.Any(item => string.Equals(item.Id, definition.Id, StringComparison.Ordinal))).ConfigureAwait(true);
        WorkflowDeleteRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision!);
        WorkflowDeleteResult deleted = await first.PostAsync<WorkflowDeleteResult>("workflows.delete", request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, deleted.Deleted.Outcome);
        Assert.Equal(EOutcome.Succeeded, deleted.Snapshot.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<WorkflowListPage>("workflows.list", token).ConfigureAwait(true)).Entries.Count == 0).ConfigureAwait(true);
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        Assert.Empty((await second.GetAsync<WorkflowListPage>("workflows.list").ConfigureAwait(true)).Entries);
        Assert.Equal(deleted, await first.PostAsync<WorkflowDeleteResult>("workflows.delete", request).ConfigureAwait(true));
        Assert.Equal(EOutcome.NotExecuted, (await second.PostAsync<ConfigurationPhaseResult>("workflows.activate", new ActivationRequest(Guid.NewGuid(), definition.Id, saved.Revision!)).ConfigureAwait(true)).Outcome);
    }

    /// <summary>両Peerが現在の二実体で通常参加するまで待ちます。</summary>
    /// <returns>実通信による参加確認です。</returns>
    private static Task ReadyAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        JsonElement a = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        JsonElement b = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        Guid firstInstance = a.GetProperty("instanceId").GetGuid(), secondInstance = b.GetProperty("instanceId").GetGuid();
        string ready = "ready";
        return a.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), ready, StringComparison.Ordinal)) == 2
            && b.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), ready, StringComparison.Ordinal)) == 2
            && a.GetProperty("peers").EnumerateArray().Any(item => item.GetProperty("instanceId").GetGuid() == secondInstance)
            && b.GetProperty("peers").EnumerateArray().Any(item => item.GetProperty("instanceId").GetGuid() == firstInstance);
    }, cancellationToken);
}
