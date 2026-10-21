using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>複数Coreへ接続した編集者の短命な座標と未保存状態を実通信で共有します。</summary>
public sealed class CollaborationPresenceTests
{
    /// <summary>移動途中と取消を別画面へ通知し、保存済みノードの配置を変更しません。</summary>
    /// <returns>実Core二つの共有と定義非変更の確認です。</returns>
    [Fact]
    public async Task CursorDragAndDirtyTravelAcrossPeersWithoutSavingAsync()
    {
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
            JsonElement state = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return state.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
        }).ConfigureAwait(true);
        WorkflowDefinition workflow = new("presence", "Presence", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }), Position: new(10, 20))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null)).ConfigureAwait(true);
        PresenceUpdate a = new(workflow.Id, Guid.NewGuid(), "host-a", "Editor A", ["pass"], 45, 65, true, ["pass"], "pass", new(300, 400));
        PresenceUpdate b = new(workflow.Id, Guid.NewGuid(), "host-b", "Editor B", [], 80, 90);
        _ = await first.NotifyAsync<JsonElement>("collaboration.update-presence", a).ConfigureAwait(true);
        _ = await second.NotifyAsync<JsonElement>("collaboration.update-presence", b).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            PresenceView[] peers = await second.GetAsync<PresenceView[]>("collaboration.presence", workflow.Id, token).ConfigureAwait(true);
            return peers.Any(item => item.Presence.UiInstanceId == a.UiInstanceId && item.Presence.Dirty && item.Presence.DragPosition == new NodePosition(300, 400))
                && peers.Any(item => item.Presence.UiInstanceId == b.UiInstanceId);
        }).ConfigureAwait(true);
        _ = await first.NotifyAsync<JsonElement>("collaboration.update-presence", a with { CursorX = null, CursorY = null, Dirty = false, DragNodeId = null, DragPosition = null }).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            PresenceView[] peers = await second.GetAsync<PresenceView[]>("collaboration.presence", workflow.Id, token).ConfigureAwait(true);
            return peers.Any(item => item.Presence.UiInstanceId == a.UiInstanceId && !item.Presence.Dirty && item.Presence.DragPosition is null);
        }).ConfigureAwait(true);
        JsonElement persisted = await second.GetAsync<JsonElement>("workflows.read", workflow.Id).ConfigureAwait(true);
        Assert.Equal(saved.Revision, persisted.GetProperty("saved").GetProperty("revision").GetString());
        Assert.Equal(10, persisted.GetProperty("saved").GetProperty("definition").GetProperty("nodes")[0].GetProperty("position").GetProperty("x").GetInt32());
    }
}
