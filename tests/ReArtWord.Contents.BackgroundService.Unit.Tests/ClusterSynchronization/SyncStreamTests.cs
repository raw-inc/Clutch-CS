using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>同期本文の実TCP配送と、元の接続許可・固定内容・所有寿命を検証します。</summary>
public sealed class SyncStreamTests
{
    /// <summary>大きい本文を連続受信し、固定した長さと全体ハッシュを維持します。</summary>
    /// <returns>実接続での完全受信と、明示終了後の接続不能確認です。</returns>
    [Fact]
    public async Task SnapshotStreamsWithItsOriginalHashAndClosesAsync()
    {
        Fixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        SyncManifest manifest = await fixture.CreateAsync().ConfigureAwait(true);
        using MemoryStream received = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Assert.True(await SyncStreamDownload.CopyAsync(manifest, fixture.Binding, "127.0.0.1", received, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(true));
        Assert.Equal(manifest.Length, received.Length);
        Assert.Equal(manifest.Hash, Convert.ToHexString(SHA256.HashData(received.ToArray())));
        await fixture.Exports.ReleaseAsync(manifest.Id, timeout.Token).ConfigureAwait(true);
        using MemoryStream after = new();
        Assert.False(await SyncStreamDownload.CopyAsync(manifest, fixture.Binding, "127.0.0.1", after, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(true));
        Assert.Equal(0, after.Length);
    }

    /// <summary>許可のどの識別子を差し替えても本文を渡さず、元の相手だけは継続できます。</summary>
    /// <param name="changed">差し替える識別子です。</param>
    /// <returns>未認証接続と正規接続の分離です。</returns>
    [Theory]
    [InlineData("token")]
    [InlineData("instance")]
    [InlineData("id")]
    public async Task ChangedIdentityCannotReadTheSnapshotAsync(string changed)
    {
        Fixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        SyncManifest original = await fixture.CreateAsync().ConfigureAwait(true);
        SyncManifest invalid = changed switch
        {
            "token" => original with { Stream = original.Stream! with { Token = Guid.NewGuid() } },
            "instance" => original with { TargetInstanceId = Guid.NewGuid() },
            _ => original with { Id = Guid.NewGuid() },
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using MemoryStream rejected = new();
        _ = await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await SyncStreamDownload.CopyAsync(invalid, fixture.Binding, "127.0.0.1", rejected, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false)).ConfigureAwait(true);
        Assert.Equal(0, rejected.Length);
        using MemoryStream accepted = new();
        Assert.True(await SyncStreamDownload.CopyAsync(original, fixture.Binding, "127.0.0.1", accepted, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(true));
        Assert.Equal(original.Length, accepted.Length);
    }

    /// <summary>途中で読む側が切断しても、同じ固定内容を変更せず再読取りできます。</summary>
    /// <returns>切断後の長さとハッシュの維持です。</returns>
    [Fact]
    public async Task DisconnectedReaderDoesNotChangeTheExportAsync()
    {
        Fixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        SyncManifest manifest = await fixture.CreateAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using (TcpClient client = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await client.ConnectAsync(IPAddress.Loopback, manifest.Stream!.Port, timeout.Token).ConfigureAwait(true);
            using NetworkStream stream = client.GetStream();
            byte[] header = Header(manifest);
            await stream.WriteAsync(header, timeout.Token).ConfigureAwait(true);
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), timeout.Token).ConfigureAwait(true);
        }
        using MemoryStream received = new();
        Assert.True(await SyncStreamDownload.CopyAsync(manifest, fixture.Binding, "127.0.0.1", received, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(true));
        Assert.Equal(manifest.Hash, Convert.ToHexString(SHA256.HashData(received.ToArray())));
    }

    /// <summary>認証ヘッダー待機中でも、所有者の終了で受付を回収できます。</summary>
    /// <returns>停止が接続待ちに妨げられない確認です。</returns>
    [Fact]
    public async Task ClosingExportCancelsAStalledConnectionAsync()
    {
        Fixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        SyncManifest manifest = await fixture.CreateAsync().ConfigureAwait(true);
        using TcpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(IPAddress.Loopback, manifest.Stream!.Port, timeout.Token).ConfigureAwait(true);
        Task release = fixture.Exports.ReleaseAsync(manifest.Id, timeout.Token);
        try
        {
            await release.WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            await release.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>旧版のマニフェストは本文を変更せず従来経路の選択を返します。</summary>
    /// <returns>旧版との混在を維持する確認です。</returns>
    [Fact]
    public async Task LegacyManifestKeepsTheDatagramPathAsync()
    {
        Fixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        SyncManifest manifest = (await fixture.CreateAsync().ConfigureAwait(true)) with { Stream = null };
        using MemoryStream received = new();
        Assert.False(await SyncStreamDownload.CopyAsync(manifest, fixture.Binding, "127.0.0.1", received, TimeSpan.FromSeconds(2)).ConfigureAwait(true));
        Assert.Equal(0, received.Length);
    }

    /// <summary>本文の過不足を成功にせず、途中受信後にUDPで取り直しません。</summary>
    /// <param name="difference">提示された長さの差です。</param>
    /// <returns>実TCP上での完全長検証です。</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task IncorrectLengthRejectsTheStreamAsync(int difference)
    {
        Fixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        SyncManifest manifest = await fixture.CreateAsync().ConfigureAwait(true);
        using MemoryStream received = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        SyncFailureException error = await Assert.ThrowsAsync<SyncFailureException>(async () =>
            await SyncStreamDownload.CopyAsync(manifest with { Length = manifest.Length + difference }, fixture.Binding,
                "127.0.0.1", received, TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false)).ConfigureAwait(true);
        Assert.Equal("Sync.ChunkMismatch", error.Code);
    }

    /// <summary>固定長の接続許可を作ります。</summary>
    /// <returns>元要求・相手実体・許可が揃ったヘッダーです。</returns>
    private static byte[] Header(SyncManifest manifest)
    {
        byte[] header = new byte[56];
        "RAWSYN01"u8.CopyTo(header);
        manifest.Id.TryWriteBytes(header.AsSpan(8, 16));
        manifest.TargetInstanceId.TryWriteBytes(header.AsSpan(24, 16));
        manifest.Stream!.Token.TryWriteBytes(header.AsSpan(40, 16));
        return header;
    }

    /// <summary>一つの実保存・固定書出し・明示NICを試験の寿命内で所有します。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SyncRecoveryPeer peer = new(new RepositorySnapshot([], []));

        /// <summary>ループバック以外へ接続しない通信条件を作ります。</summary>
        internal Fixture()
        {
            string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
            Binding = new(network, "127.0.0.1");
            PeerInfo local = peer.Target.GetPeers().Single();
            ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".");
            Exports = new(peer.Target, peer.Repository, options, TimeProvider.System, () => Binding);
        }

        /// <summary>試験専用の固定書出しです。</summary>
        internal SyncExportStore Exports
        {
            get;
        }

        /// <summary>試験専用の明示NICです。</summary>
        internal LocalNetworkBinding Binding
        {
            get;
        }

        /// <summary>UDP一メッセージに収まらない本文を保存して書き出します。</summary>
        /// <returns>試験が所有する固定内容です。</returns>
        internal async Task<SyncManifest> CreateAsync(CancellationToken cancellationToken = default)
        {
            _ = await peer.Repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("stream-probe", "value", null, NodeValue.FromObject(new string('x', 5 * 1024 * 1024)))], cancellationToken).ConfigureAwait(false);
            PeerInfo local = peer.Target.GetPeers().Single();
            return await Exports.CreateAsync(local, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>作業接続を先に閉じ、保存とUDPを最後に解放します。</summary>
        /// <returns>試験資源の終了確認です。</returns>
        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await ((IAsyncDisposable)Exports).DisposeAsync().ConfigureAwait(false);
            await ((IAsyncDisposable)peer).DisposeAsync().ConfigureAwait(false);
        }
    }
}
