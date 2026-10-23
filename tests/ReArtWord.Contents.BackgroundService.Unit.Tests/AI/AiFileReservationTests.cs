using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>外部の結果不明と、終了済みローカルファイル予約を区別して検証します。</summary>
public sealed class AiFileReservationTests
{
    /// <summary>API送信前でも、外部プロセスの停止を確認できない例外は未実行として解放しません。</summary>
    /// <returns>異常境界の注入による不明結果と排他保持です。</returns>
    [Fact]
    public async Task UnconfirmedProcessStopKeepsTheReservationAsync()
    {
        FileTestPeer peer = await PeerAsync();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        MediaOptions media = new(Environment.ProcessPath!, Environment.ProcessPath!, new([peer.Root]));
        IActionNode node = (IActionNode)AiNodeRegistration.Create(new UnconfirmedSecretProcess(), new AiOptions(server.BaseUri), media).Single(item => item.Descriptor.Type is "ai.audio.generate");
        NodeExecutionContext context = Context(peer, server, "ai.audio.generate", "gpt-4o-mini-tts", Path.Combine(peer.Root, "held.wav"));
        NodeResult result = await peer.Transactions.ExecuteFileAsync(node, context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("ai.stopUnconfirmed", result.ReasonCode);
        Assert.False(result.FileAccessCompleted);
        Assert.Single((await FileReservationLedger.ReadAsync(peer.Repository, "file:reservations").ConfigureAwait(true)).Active);
        Assert.Empty(server.Requests);
    }

    /// <summary>全生成APIで不明結果を保持しつつ予約を解放し、次の明示要求を妨げません。</summary>
    /// <returns>実HTTP・SQLite・ファイル予約境界の確認です。</returns>
    [Theory]
    [InlineData("ai.audio.generate", "gpt-4o-mini-tts")]
    [InlineData("ai.audio.elevenlabs.generate", "eleven_multilingual_v2")]
    [InlineData("ai.audio.elevenlabs.v3.generate", "eleven_v3")]
    [InlineData("ai.audio.elevenlabs.v4.generate", "eleven_v4")]
    [InlineData("ai.audio.elevenlabs.soundEffects", "eleven_text_to_sound_v2")]
    [InlineData("ai.audio.qwen.generate", "qwen3-tts-vc-2026-01-22")]
    [InlineData("ai.audio.qwen.tts.generate", "qwen3-tts-flash")]
    [InlineData("ai.audio.qwen.audio3.generate", "qwen-audio-3.0-tts-flash")]
    [InlineData("ai.picture.generate", "gpt-image-1")]
    [InlineData("ai.video.generate", "wan2.6-t2v")]
    public async Task ProviderFailureReleasesOnlyTheLocalReservationAsync(string type, string model)
    {
        FileTestPeer peer = await PeerAsync();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}", 500)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        string output = Path.Combine(peer.Root, "existing.wav");
        await File.WriteAllTextAsync(output, "original").ConfigureAwait(true);
        IActionNode node = Node(peer, server, type);
        NodeExecutionContext context = Context(peer, server, type, model, output);
        NodeResult first = await peer.Transactions.ExecuteFileAsync(node, context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, first.Outcome);
        Assert.True(first.FileAccessCompleted);
        Assert.Single(server.Requests);
        Assert.Empty((await FileReservationLedger.ReadAsync(peer.Repository, "file:reservations").ConfigureAwait(true)).Active);
        Assert.Empty(await peer.Repository.ListAsync("peer:one:file-recovery").ConfigureAwait(true));
        NodeResult next = await peer.Transactions.ExecuteFileAsync(node, context with { ExecutionId = Guid.NewGuid() }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, next.Outcome);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("original", await File.ReadAllTextAsync(output).ConfigureAwait(true));
    }

    /// <summary>送信後のタイムアウト・取消でも、閉じたローカルアクセスの予約を残しません。</summary>
    /// <returns>外部結果を成功化・自動再送しない取消境界です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationReleaseTheOwnedReservationAsync(bool cancel)
    {
        FileTestPeer peer = await PeerAsync();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AiWireFixture server = new(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return AiWireResponse.Json("{}", 500);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        const string type = "ai.audio.qwen.tts.generate";
        NodeExecutionContext context = Context(peer, server, type, "qwen3-tts-flash", Path.Combine(peer.Root, "timeout.wav"), 500);
        Task<NodeResult> pending = peer.Transactions.ExecuteFileAsync(Node(peer, server, type), context, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            if (cancel)
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
            }
            NodeResult result = await pending.ConfigureAwait(true);
            Assert.Equal(EOutcome.Unknown, result.Outcome);
            Assert.True(result.FileAccessCompleted);
            Assert.Empty((await FileReservationLedger.ReadAsync(peer.Repository, "file:reservations").ConfigureAwait(true)).Active);
            Assert.Single(server.Requests);
        }
        finally
        {
            release.TrySetResult();
            await pending.ConfigureAwait(true);
        }
    }

    /// <summary>検証中は外部サービスや本番の保存先を使用しません。</summary>
    /// <returns>独立した実クラスタです。</returns>
    private static Task<FileTestPeer> PeerAsync()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "one", ((IPEndPoint)socket.Client.LocalEndPoint!).Port, true);
    }

    /// <summary>HTTP失敗の前にプローブを実行しない試験用の構成です。</summary>
    /// <returns>実際の公開生成ノードです。</returns>
    private static IActionNode Node(FileTestPeer peer, AiWireFixture server, string type)
    {
        MediaOptions media = new(Environment.ProcessPath!, Environment.ProcessPath!, new([peer.Root]));
        return (IActionNode)AiNodeRegistration.Create(new AiTestSecrets(), new AiOptions(server.BaseUri), media).Single(item => string.Equals(item.Descriptor.Type, type, StringComparison.Ordinal));
    }

    /// <summary>出力・通信・実行所有者を試験Peerへ固定します。</summary>
    /// <returns>同じファイルを次の明示要求でも使用する設定です。</returns>
    private static NodeExecutionContext Context(FileTestPeer peer, AiWireFixture server, string type, string model, string output, int timeout = 10000)
    {
        Dictionary<string, object> settings = AiOutputPolicyTests.Settings(type, model, output);
        settings["timeoutMs"] = timeout;
        return NodeTestContext.Create(AiGeneratorTests.Settings(server, settings), NodeValue.FromObject("hello")) with { PeerId = peer.Cluster.LocalPeerId, ExecutionId = Guid.NewGuid() };
    }

    /// <summary>送信前の依存解決境界へ、プロセス停止未確認の障害を注入します。</summary>
    private sealed class UnconfirmedSecretProcess : ISecretStore
    {
        /// <summary>ローカル依存の停止を確認できなかった状況です。</summary>
        /// <returns>停止未確認の例外です。</returns>
        public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromException<string?>(new ProcessStopUnconfirmedException("Fixture stop is unconfirmed."));

        /// <summary>試験では秘密値を変更しません。</summary>
        /// <returns>未対応です。</returns>
        public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new NotSupportedException());
    }
}
