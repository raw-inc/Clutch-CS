using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>定義本文の大きさに左右されない軽量一覧と単体取得の省略を検証します。</summary>
public sealed class WorkflowListingTests
{
    /// <summary>一覧はバイト上限で続き、巨大な単体定義を偽の編集候補にしません。</summary>
    /// <returns>保存済み定義の管理読取検証です。</returns>
    [Fact]
    public async Task LargeDefinitionsHaveBoundedListAndExplicitDetailOmission()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-workflow-page-" + Guid.NewGuid().ToString("N"));
        using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        try
        {
            ManagementTestPeer peer = new(root, Guid.NewGuid().ToString("N"), "peer", port, true);
            await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
            for (int index = 0; index < 45; index++)
            {
                WorkflowDefinition definition = new("wf" + index, new string('名', 1000), [], []);
                StoredWorkflow stored = new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto);
                await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflows", definition.Id, null, NodeValue.FromObject(stored))]).ConfigureAwait(true);
            }
            List<string> ids = [];
            string? cursor = null;
            int pages = 0;
            do
            {
                WorkflowListPage page = await WorkflowListing.ReadAsync(peer.Runtime, new(100, cursor, 16384)).ConfigureAwait(true);
                Assert.NotEmpty(page.Entries);
                Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length <= 16384);
                ids.AddRange(page.Entries.Select(static item => item.Id!));
                cursor = page.Cursor;
                pages++;
            }
            while (cursor is not null && pages < 20);
            Assert.Null(cursor);
            Assert.True(1 < pages);
            Assert.Equal(45, ids.Distinct().Count());
            WorkflowDefinition huge = new("large", new string('x', 1200000), [], []);
            StoredWorkflow hugeStored = new(huge, ContractJson.Hash(huge), DateTimeOffset.UtcNow, EActivationPolicy.Explicit);
            await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflows", huge.Id, null, NodeValue.FromObject(hugeStored))]).ConfigureAwait(true);
            JsonElement view = JsonSerializer.SerializeToElement(WorkflowListing.View(hugeStored, peer.Runtime), ContractJson.Options);
            Assert.Equal(JsonValueKind.Null, view.GetProperty("saved").ValueKind);
            Assert.Equal("large", view.GetProperty("summary").GetProperty("id").GetString());
            Assert.Equal("workflow.details.size", view.GetProperty("detailReason").GetString());
            Assert.Equal("explicit", view.GetProperty("summary").GetProperty("savedActivationPolicy").GetString());
            using MemoryStream complete = new();
            int? offset = 0;
            string? hash = null;
            do
            {
                WorkflowDefinitionChunk chunk = await WorkflowListing.ChunkAsync(peer.Runtime, new(huge.Id, hugeStored.Revision, Offset: offset!.Value)).ConfigureAwait(true);
                Assert.Equal(offset, chunk.Offset);
                Assert.True(JsonSerializer.SerializeToUtf8Bytes(chunk, ContractJson.Options).Length < 131072);
                hash ??= chunk.Hash;
                Assert.Equal(hash, chunk.Hash);
                complete.Write(Convert.FromBase64String(chunk.DataBase64));
                offset = chunk.NextOffset;
            }
            while (offset is not null);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(complete.ToArray())));
            WorkflowDefinition restored = JsonSerializer.Deserialize<WorkflowDefinition>(complete.ToArray(), ContractJson.Options)!;
            Assert.Equal(huge.Name, restored.Name);
            await Assert.ThrowsAsync<ArgumentException>(() => WorkflowListing.ChunkAsync(peer.Runtime, new(huge.Id, "stale"))).ConfigureAwait(true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
