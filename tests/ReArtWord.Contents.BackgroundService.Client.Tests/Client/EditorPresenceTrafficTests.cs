using System.Net;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>他の編集者がいても、無操作時の共有を高頻度で再送しません。</summary>
public sealed class EditorPresenceTrafficTests(ITestOutputHelper output)
{
    /// <summary>60秒相当の移動と停止を、実クライアントのHTTP境界で計数します。</summary>
    /// <returns>操作時のみの送信と、停止後の在席維持だけの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdlePeersDoNotCauseContinuousPresenceWritesAsync(bool realtime)
    {
        EditorTestClock clock = new();
        int posts = 0, reads = 0, movingPosts = 0;
        long sentBytes = 0;
        PresenceView other = new("peer-b", Guid.NewGuid(), new("flow", Guid.NewGuid(), "other", "editor", []), clock.GetUtcNow().AddHours(1));
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                sentBytes += (await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false)).Length;
            }
            else
            {
                reads++;
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { other }, options: ContractJson.Options) };
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        model.Import(new("flow", "name", [], []));
        EditorPresence presence = new(model, new(client, new(clock))) { Realtime = realtime };
        for (int tick = 0; tick < 400; tick++)
        {
            if (tick < 100)
            {
                presence.SetPointer(new(tick, tick), null, null, tick == 0);
            }
            await presence.RefreshAsync("host").ConfigureAwait(true);
            if (tick == 99)
            {
                movingPosts = posts;
            }
            clock.Advance(TimeSpan.FromMilliseconds(150));
        }
        Assert.InRange(posts - movingPosts, 2, 3);
        if (realtime)
        {
            Assert.Equal(100, movingPosts);
            Assert.InRange(reads, 140, 155);
        }
        else
        {
            Assert.Equal(1, movingPosts);
            Assert.InRange(reads, 55, 60);
        }
        output.WriteLine($"realtime={realtime}; simulatedSeconds=60; POST={posts}; GET={reads}; requestBodyBytes={sentBytes}; idlePOST={posts - movingPosts}");
    }
}
