using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core間で一人の連続編集と競合解消が巻き戻らないことを確認します。</summary>
public sealed class CollaborationDraftTests
{
    /// <summary>移動・解消・保存後に両Peerを繰り返し照会しても競合を再生成しません。</summary>
    /// <returns>実プロセスとUDPによる草稿同期の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialEditsAndResolutionStayStableAcrossCoreProcessesAsync(bool forceSave)
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
        WorkflowDefinition original = new("draft", "initial", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }), Position: new(10, 20))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), original, null)).ConfigureAwait(true);
        DraftView view = await ReadAsync(first).ConfigureAwait(true);
        for (int index = 0; index < 6; index++)
        {
            WorkflowDefinition moved = view.Definition with { Nodes = [view.Definition.Nodes[0] with { Position = new(100 + index, 200 + index) }] };
            view = await first.NotifyAsync<DraftView>("collaboration.changes", Change(view, moved)).ConfigureAwait(true);
            await AssertBothAsync(first, second, view).ConfigureAwait(true);
        }
        DraftView baseline = view;
        view = await first.NotifyAsync<DraftView>("collaboration.changes", Change(baseline, baseline.Definition with { Name = "one" })).ConfigureAwait(true);
        await AssertBothAsync(first, second, view).ConfigureAwait(true);
        view = await second.NotifyAsync<DraftView>("collaboration.changes", Change(baseline, baseline.Definition with { Name = "two" })).ConfigureAwait(true);
        Assert.NotEmpty(view.Conflicts);
        string conflictedVersion = view.SessionVersion;
        await CorePeer.WaitAsync(async token =>
        {
            view = await first.GetAsync<DraftView>("collaboration.draft", original.Id, token).ConfigureAwait(true);

            // SessionVersionは本文だけの識別子なので、競合情報の到着も確認します。
            return string.Equals(view.SessionVersion, conflictedVersion, StringComparison.Ordinal) && view.Conflicts.Count != 0;
        }).ConfigureAwait(true);
        Assert.NotEmpty(view.Conflicts);
        DraftChangeRequest resolution = Change(view, view.Definition with { Name = "chosen" }) with
        {
            Resolutions = new Dictionary<string, EEditResolution> { ["/name"] = EEditResolution.UseEdited },
        };
        if (forceSave)
        {
            saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), resolution.Edited, saved.Revision)).ConfigureAwait(true);
            Assert.Equal(Contracts.Execution.EOutcome.Succeeded, saved.Saved.Outcome);
        }
        view = await first.NotifyAsync<DraftView>("collaboration.changes", resolution).ConfigureAwait(true);
        Assert.Empty(view.Conflicts);
        await AssertBothAsync(first, second, view, waitForResolution: true).ConfigureAwait(true);
        WorkflowSaveResult final = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), view.Definition, saved.Revision)).ConfigureAwait(true);
        Assert.Equal(Contracts.Execution.EOutcome.Succeeded, final.Saved.Outcome);
        for (int index = 0; index < 5; index++)
        {
            await AssertBothAsync(first, second, view).ConfigureAwait(true);
        }
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await AssertBothAsync(first, second, view).ConfigureAwait(true);
    }

    /// <summary>両方の実Coreを照会し、競合も同時に確認します。</summary>
    /// <returns>草稿の到達確認です。</returns>
    private static async Task AssertBothAsync(CorePeer first, CorePeer second, DraftView expected, bool waitForResolution = false)
    {
        await CorePeer.WaitAsync(async token =>
        {
            DraftView a = await first.GetAsync<DraftView>("collaboration.draft", expected.WorkflowId, token).ConfigureAwait(true);
            DraftView b = await second.GetAsync<DraftView>("collaboration.draft", expected.WorkflowId, token).ConfigureAwait(true);
            bool arrived = string.Equals(a.SessionVersion, expected.SessionVersion, StringComparison.Ordinal) && string.Equals(b.SessionVersion, expected.SessionVersion, StringComparison.Ordinal);
            if (waitForResolution && (a.Conflicts.Count != 0 || b.Conflicts.Count != 0))
            {
                return false;
            }
            if (arrived)
            {
                Assert.Empty(a.Conflicts);
                Assert.Empty(b.Conflicts);
            }
            return arrived;
        }).ConfigureAwait(true);
    }

    /// <summary>共有草稿を取得します。</summary>
    /// <returns>現在の候補です。</returns>
    private static Task<DraftView> ReadAsync(CorePeer peer) => peer.GetAsync<DraftView>("collaboration.draft", "draft");

    /// <summary>確認した編集元を固定した要求です。</summary>
    /// <returns>再送可能な変更です。</returns>
    private static DraftChangeRequest Change(DraftView view, WorkflowDefinition edited)
        => new(Guid.NewGuid(), view.WorkflowId, Guid.NewGuid(), view.BaseRevision, view.SessionVersion, view.Definition, edited, new Dictionary<string, EEditResolution>());
}
