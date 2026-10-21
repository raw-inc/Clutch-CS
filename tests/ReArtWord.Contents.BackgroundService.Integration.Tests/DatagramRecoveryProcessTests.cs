using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>未同定の末尾や未完了の通知が実Coreへ集中しても、共有保存の全段階を確認します。</summary>
public sealed class DatagramRecoveryProcessTests
{
    /// <summary>三つの実プロセスに断片欠落を繰り返し与え、設定反映の確認不能を再現させません。</summary>
    /// <returns>実UDP・永続保存・スナップショット・有効化の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteMessagesDoNotBlockReplicatedWorkflowActivationAsync(bool notifications)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "first", port, true);
        CorePeer second = new(cluster, "second", port, false);
        CorePeer third = new(cluster, "third", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable thirdLifetime = third.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await third.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true))
            .GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 3).ConfigureAwait(true);
        using UdpClient broadcast = new(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true };
        string? revision = null;
        for (int pass = 0; pass < 4; pass++)
        {
            await SendIncompleteMessagesAsync(broadcast, cluster, port, notifications).ConfigureAwait(true);
            NodeDefinition payload = new("payload", "value.constant", JsonSerializer.SerializeToElement(new { text = new string('x', 4096) + pass }));
            WorkflowDefinition definition = new("delivery-recovery", "Delivery recovery " + pass, [payload], []);
            WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, revision)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
            revision = saved.Revision;
            foreach (CorePeer peer in new[] { first, second, third })
            {
                await CorePeer.WaitAsync(async token => string.Equals((await peer.GetAsync<JsonElement>("workflows.read", definition.Id, token).ConfigureAwait(true))
                    .GetProperty("effectiveRevision").GetString(), saved.Revision, StringComparison.Ordinal)).ConfigureAwait(true);
            }
        }
    }

    /// <summary>他宛先の末尾または一時通知の先頭だけを、現行protocolの正しい本文から作ります。</summary>
    /// <returns>受信上限を超える未完了配送の送信です。</returns>
    private static async Task SendIncompleteMessagesAsync(UdpClient broadcast, string cluster, int port, bool notifications)
    {
        IPEndPoint endpoint = new(IPAddress.Parse("127.255.255.255"), port);
        for (int index = 0; index < 40; index++)
        {
            WireMessage message = new(Guid.NewGuid(), cluster, "other-sender", Guid.NewGuid(), "reply", "other-recipient", null,
                JsonSerializer.SerializeToElement(new string('x', 4096)), Guid.NewGuid());
            int part = 1;
            if (notifications)
            {
                message = message with { Kind = "transient", TargetId = null, TargetInstanceId = null };
                part = 0;
            }
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
            byte[] packet = JsonSerializer.SerializeToUtf8Bytes(new
            {
                message.Id,
                Index = part,
                Count = (body.Length + 863) / 864,
                Length = body.Length,
                Hash = Convert.ToHexString(SHA256.HashData(body)),
                Payload = body.AsMemory(part * 864, 864).ToArray(),
            });
            await broadcast.SendAsync(packet, endpoint).ConfigureAwait(true);
        }
    }
}
