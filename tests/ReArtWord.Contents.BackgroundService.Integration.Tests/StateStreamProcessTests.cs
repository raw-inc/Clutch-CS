using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実Coreプロセスで、State複製と変更通知TCPの寿命を検証します。</summary>
public sealed class StateStreamProcessTests
{
    /// <summary>別Peerの変更が購読へ届き、WF状態とクラスタ状態を混同せず、切断後に購読を回収します。</summary>
    /// <returns>実UDP保存・複製と実TCP変更配信の検証です。</returns>
    [Fact]
    public async Task ObservesReplicatedStateAndReleasesConnectionAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        InternalDataTarget shared = new(string.Empty, EInternalDataScope.Cluster, "cue");
        InternalDataWriteResult initial = await first.PostAsync<InternalDataWriteResult>("internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), shared, null, NodeValue.FromObject("seed")), timeout.Token).ConfigureAwait(true);
        Assert.True(initial.Result.Applied);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        InternalDataReadResult restored = await second.GetAsync<InternalDataReadResult>("internal-data.read", shared, timeout.Token).ConfigureAwait(true);
        Assert.Equal("seed", restored.ConfirmedValue!.GetString());
        InternalDataTarget workflow = new("one", EInternalDataScope.Shared, "cue");
        StateStreamTicket ticket = await first.NotifyAsync<StateStreamTicket>("state-stream.open", Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, ticket.Port, timeout.Token).ConfigureAwait(true);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Convert.FromHexString(ticket.Token), timeout.Token).ConfigureAwait(true);
            await StateStreamWire.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new[] { new StateStreamTarget("cluster", shared), new StateStreamTarget("workflow", workflow) }, ContractJson.Options), timeout.Token).ConfigureAwait(true);
            byte[] buffer = new byte[StateStreamWire.MaximumBytes + 4];
            Dictionary<string, StateStreamView> values = new(StringComparer.Ordinal);
            while (values.Count < 2)
            {
                await ReadAsync(stream, buffer, values, timeout.Token).ConfigureAwait(true);
            }
            Assert.True(values["cluster"].Exists);
            Assert.Equal("seed", values["cluster"].Text);
            Assert.False(values["workflow"].Exists);
            InternalDataWriteResult saved = await second.PostAsync<InternalDataWriteResult>("internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), shared, restored.ConfirmedRevision, NodeValue.FromObject("shared value")), timeout.Token).ConfigureAwait(true);
            Assert.True(saved.Result.Applied, saved.Result.Reason);
            await ReadAsync(stream, buffer, values, timeout.Token).ConfigureAwait(true);
            Assert.Equal("shared value", values["cluster"].Text);
            Assert.False(values["workflow"].Exists);
            saved = await second.PostAsync<InternalDataWriteResult>("internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), workflow, null, NodeValue.FromBytes(new byte[12000])), timeout.Token).ConfigureAwait(true);
            Assert.True(saved.Result.Applied, saved.Result.Reason);
            await ReadAsync(stream, buffer, values, timeout.Token).ConfigureAwait(true);
            Assert.Contains("Binary", values["workflow"].Text, StringComparison.Ordinal);
            Assert.True(values["workflow"].Text.Length < 80);
            Assert.Equal("shared value", values["cluster"].Text);
        }
        await CorePeer.WaitAsync(async token => await first.GetAsync<int>("state-stream.status", token).ConfigureAwait(true) == 0, timeout.Token).ConfigureAwait(true);
        InternalDataReadResult persisted = await first.GetAsync<InternalDataReadResult>("internal-data.read", shared, timeout.Token).ConfigureAwait(true);
        Assert.Equal("shared value", persisted.ConfirmedValue!.GetString());
    }

    /// <summary>有限サイズの実フレームを読み、カードの最新表示を置換します。</summary>
    /// <returns>一つの配信バッチの読取りです。</returns>
    private static async Task ReadAsync(Stream stream, Memory<byte> buffer, Dictionary<string, StateStreamView> values, CancellationToken cancellationToken = default)
    {
        int length = await StateStreamWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(true);
        Assert.NotEqual(0, length);
        foreach (StateStreamView view in JsonSerializer.Deserialize<StateStreamView[]>(buffer.Span.Slice(4, length), ContractJson.Options)!)
        {
            values[view.Id] = view;
        }
    }
}
