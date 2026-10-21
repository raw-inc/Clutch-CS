using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>追加表示だけが観測し、別版・完了済み・取得不能を処理中と表示しません。</summary>
public sealed class EditorNodeActivityTests
{
    /// <summary>子孫の処理中状態を該当配置だけへ集約し、定義編集では観測しません。</summary>
    /// <returns>複数配置・入れ子・終了の表示境界です。</returns>
    [Fact]
    public async Task RecipeActivityAggregatesDescendantsPerInstanceAsync()
    {
        EditorTestClock clock = new();
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("flow", "Flow", [new("first", SubworkflowInterface.CallType("recipe"), settings), new("second", SubworkflowInterface.CallType("recipe"), settings)], []);
        string revision = ContractJson.Hash(definition);
        EditorDraft draft = new(definition, definition, revision, definition, null, revision, false, []);
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 1, clock.GetUtcNow(), clock.GetUtcNow());
        string nested = RecipeNodeIdentity.Child(RecipeNodeIdentity.Child("first", "nested"), "wait");
        WorkflowNodeActivity reply = new(definition.Id, peer.PeerId, peer.InstanceId, [new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, revision, nested, peer.PeerId, clock.GetUtcNow())]);
        int requests = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, options: ContractJson.Options) });
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorNodeActivity activity = new(new(client, new(clock)));
        await activity.RefreshAsync("cluster", draft, [peer], true).ConfigureAwait(true);
        Assert.True(activity.IsRunning("cluster", draft, "first"));
        Assert.False(activity.IsRunning("cluster", draft, "second"));
        reply = reply with { Nodes = [] };
        clock.Advance(TimeSpan.FromSeconds(1));
        await activity.RefreshAsync("cluster", draft, [peer], true).ConfigureAwait(true);
        Assert.False(activity.IsRunning("cluster", draft, "first"));
        int before = requests;
        WorkflowDefinition template = definition with { IsRecipe = true };
        await activity.RefreshAsync("cluster", draft with { Document = template, Base = template }, [peer], true).ConfigureAwait(true);
        Assert.Equal(before, requests);
    }

    /// <summary>発光は同じ実体・保存版の実処理だけに限定し、未操作の表示で実行要求を送りません。</summary>
    /// <returns>処理終了と鮮度の境界です。</returns>
    [Fact]
    public async Task ActivityRequiresOptInAndExpiresUnconfirmedPeersAsync()
    {
        EditorTestClock clock = new();
        WorkflowDefinition definition = new("flow", "name", [new("node", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1000 }))], []);
        string revision = ContractJson.Hash(definition);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(definition, revision, clock.GetUtcNow(), EActivationPolicy.Auto)).ConfigureAwait(true);
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 1, clock.GetUtcNow(), clock.GetUtcNow());
        NodeValueObservation observation = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, revision, "node", peer.PeerId, clock.GetUtcNow());
        WorkflowNodeActivity reply = new(definition.Id, peer.PeerId, peer.InstanceId, [observation]);
        bool fail = false;
        int requests = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/activity", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (fail)
            {
                throw new HttpRequestException();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, options: ContractJson.Options) });
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorNodeActivity activity = new(new(client, new(clock)));
        await activity.RefreshAsync("cluster", model.Draft, [peer], false).ConfigureAwait(true);
        Assert.Equal(0, requests);
        await activity.RefreshAsync("cluster", model.Draft, [peer], true).ConfigureAwait(true);
        Assert.True(activity.IsRunning("cluster", model.Draft, "node"));
        Assert.False(activity.IsRunning("other", model.Draft, "node"));
        model.ChangeNode("node", node => node with { Position = new(100, 200) });
        Assert.True(activity.IsRunning("cluster", model.Draft, "node"));
        model.ChangeNode("node", node => node with { LogicalName = "音声の待機" });
        Assert.True(activity.IsRunning("cluster", model.Draft, "node"));
        model.ChangeNode("node", node => node with { Settings = JsonSerializer.SerializeToElement(new { milliseconds = 2000 }) });
        Assert.False(activity.IsRunning("cluster", model.Draft, "node"));
        model.Change(_ => definition);
        fail = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        await activity.RefreshAsync("cluster", model.Draft, [peer], true).ConfigureAwait(true);
        Assert.False(activity.IsRunning("cluster", model.Draft, "node"));
        fail = false;
        reply = reply with { Nodes = [observation with { CompletedAt = clock.GetUtcNow() }] };
        clock.Advance(TimeSpan.FromSeconds(1));
        await activity.RefreshAsync("cluster", model.Draft, [peer], true).ConfigureAwait(true);
        Assert.False(activity.IsRunning("cluster", model.Draft, "node"));
        reply = reply with { Nodes = [observation], InstanceId = Guid.NewGuid() };
        clock.Advance(TimeSpan.FromSeconds(1));
        await activity.RefreshAsync("cluster", model.Draft, [peer], true).ConfigureAwait(true);
        Assert.False(activity.IsRunning("cluster", model.Draft, "node"));
    }
}
