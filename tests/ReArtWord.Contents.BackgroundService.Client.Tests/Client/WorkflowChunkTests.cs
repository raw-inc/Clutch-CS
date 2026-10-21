using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>大きいワークフローを検査済み全体としてのみ公開します。</summary>
public sealed class WorkflowChunkTests
{
    /// <summary>途中でUI接続先を変えても元Peerで版を固定し、全体ハッシュ後に使用します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteDocumentRequiresMatchingHashAsync(bool corrupt)
    {
        WorkflowDefinition definition = new("flow", "分割された定義", [new("n", "value", JsonSerializer.SerializeToElement(new { value = 9007199254740993L }))], []);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(definition, ContractJson.Options);
        string hash = Convert.ToHexString(SHA256.HashData(content));
        string revision = "saved-revision";
        int calls = 0;
        ManagementApi? api = null;
        using ClientHttpHandler handler = new(async (request, cancellation) =>
        {
            Assert.Equal("original", request.Headers.GetValues("X-Contents-Peer").Single());
            if (request.Method == HttpMethod.Get)
            {
                WorkflowListSummary summary = new("flow", "分割された定義", revision, null, DateTimeOffset.UtcNow, 1, true, "id-hash", "workflow.details.size", EActivationPolicy.Explicit);
                return Reply(new WorkflowView(null, null, null, summary, "workflow.details.size"));
            }
            WorkflowDefinitionChunkRequest chunk = JsonSerializer.Deserialize<WorkflowDefinitionChunkRequest>(await request.Content!.ReadAsStringAsync(cancellation).ConfigureAwait(true), ContractJson.Options)!;
            Assert.Equal(revision, chunk.Revision);
            Assert.False(chunk.Effective);
            calls++;
            api!.SelectedPeerId = "other";
            int length = Math.Min(23, content.Length - chunk.Offset);
            int? next = null;
            if (chunk.Offset + length < content.Length)
            {
                next = chunk.Offset + length;
            }
            string expectedHash = hash;
            if (corrupt)
            {
                expectedHash = new string('0', 64);
            }
            return Reply(new WorkflowDefinitionChunk(revision, chunk.Offset, content.Length, expectedHash, Convert.ToBase64String(content.AsSpan(chunk.Offset, length)), next));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "original" };
        if (corrupt)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowReader.ReadAsync(api, "flow")).ConfigureAwait(true);
        }
        else
        {
            WorkflowView result = await WorkflowReader.ReadAsync(api, "flow").ConfigureAwait(true);
            Assert.Equal(9007199254740993L, result.RequireSaved().Definition.Nodes[0].Settings.GetProperty("value").GetInt64());
            Assert.Equal(EActivationPolicy.Explicit, result.RequireSaved().ActivationPolicy);
        }
        Assert.True(1 < calls);
    }

    /// <summary>試験用の構成応答を共通JSON設定で生成します。</summary>
    /// <returns>呼出元が破棄する応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json") };
}
