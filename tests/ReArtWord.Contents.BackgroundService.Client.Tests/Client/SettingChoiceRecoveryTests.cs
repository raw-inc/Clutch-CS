using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>候補の復旧と再起動を、保存値や別入力欄の取消しに影響させません。</summary>
public sealed class SettingChoiceRecoveryTests
{
    private static readonly string[] peers = ["peer"];
    private static readonly string[] addresses = ["192.0.2.10"];

    /// <summary>画面が再描画を省いていても、期限到来を一度通知し候補の復帰を確認できます。</summary>
    /// <returns>実通信境界と表示世代の確認です。</returns>
    [Fact]
    public async Task ExpiredChoicesNotifyOnceAndRecoverAsync()
    {
        ChoiceClock clock = new();
        int requests = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            requests++;
            return Task.FromResult(Reply(Network(requests.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost/") };
        ManagementApi api = new(http, new(clock)) { SelectedClusterId = "cluster" };
        SettingChoiceProvider provider = new(api);
        Assert.Equal("1", Assert.Single((await provider.GetAsync(Node()).ConfigureAwait(true)).Interfaces).Id);
        int first = provider.Version;
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(first + 1, provider.Version);
        Assert.Equal(first + 1, provider.Version);
        Assert.Equal("2", Assert.Single((await provider.GetAsync(Node()).ConfigureAwait(true)).Interfaces).Id);
        Assert.Equal(2, requests);
        await provider.GetAsync(Node()).ConfigureAwait(true);
        Assert.Equal(2, requests);
    }

    /// <summary>入力欄を閉じた取消しで他の入力欄が待つNIC照会を失敗にしません。</summary>
    /// <returns>共有照会と個別待機の取消しの確認です。</returns>
    [Fact]
    public async Task CancellingOneConsumerDoesNotCancelSharedInventoryAsync()
    {
        TaskCompletionSource<HttpResponseMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken transportToken = default;
        int requests = 0;
        using ClientHttpHandler handler = new((_, token) =>
        {
            requests++;
            transportToken = token;
            return reply.Task;
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost/") };
        SettingChoiceProvider provider = new(new(http) { SelectedClusterId = "cluster" });
        using CancellationTokenSource cancelled = new();
        using HttpResponseMessage restored = Reply(Network("restored"));
        Task<SettingNetworkChoices> first = provider.GetAsync(Node(), cancellationToken: cancelled.Token);
        Task<SettingNetworkChoices> second = provider.GetAsync(Node());
        try
        {
            await cancelled.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).ConfigureAwait(true);
            Assert.False(transportToken.IsCancellationRequested);
            reply.SetResult(restored);
            Assert.Equal("restored", Assert.Single((await second.ConfigureAwait(true)).Interfaces).Id);
            Assert.Equal(1, requests);
        }
        finally
        {
            reply.TrySetCanceled();
            await ((Task)Task.WhenAll(first, second)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>同じPeer IDの再起動で旧照会を破棄し、遅延応答を新しい候補へ混ぜません。</summary>
    /// <returns>接続実体をまたぐ照会結果の確認です。</returns>
    [Fact]
    public async Task RestartInvalidatesCacheAndRejectsLateInventoryAsync()
    {
        Guid instance = Guid.NewGuid();
        TaskCompletionSource<HttpResponseMessage> old = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            string network = "/api/network";
            if (string.Equals(request.RequestUri!.AbsolutePath, network, StringComparison.Ordinal))
            {
                requests++;
                return requests == 1 ? old.Task
                                     : Task.FromResult(Reply(Network("new")));
            }
            PeerInfo peer = new("peer", instance, "host", "127.0.0.1", 12345, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
            return Task.FromResult(Reply(new ServiceStatus("peer", instance, "peer", EPeerStatus.Ready, [peer], [], [], null, JsonSerializer.SerializeToElement(new { }))));
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost/") };
        ManagementApi api = new(http);
        ConnectionState connection = new(api);
        await connection.SelectTargetAsync("cluster", "peer").ConfigureAwait(true);
        SettingChoiceProvider provider = new(api, connection);
        using HttpResponseMessage stale = Reply(Network("old"));
        Task<SettingNetworkChoices> pending = provider.GetAsync(Node());
        try
        {
            int unchanged = provider.Version;
            await connection.RefreshAsync().ConfigureAwait(true);
            Assert.Equal(unchanged, provider.Version);
            instance = Guid.NewGuid();
            await connection.RefreshAsync().ConfigureAwait(true);
            Assert.Equal("new", Assert.Single((await provider.GetAsync(Node()).ConfigureAwait(true)).Interfaces).Id);
            old.SetResult(stale);
            Assert.NotNull((await pending.ConfigureAwait(true)).Problem);
            Assert.Equal("new", Assert.Single((await provider.GetAsync(Node()).ConfigureAwait(true)).Interfaces).Id);
            Assert.Equal(2, requests);
        }
        finally
        {
            old.TrySetCanceled();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>候補更新が保存値を変更しない固定設定です。</summary>
    /// <returns>一Peerを明示した通信ノードです。</returns>
    private static NodeDefinition Node() => new("listen", "http.listen", JsonSerializer.SerializeToElement(new { interfaceId = "saved", localAddress = "192.0.2.10" }), Target: new(EExecutionScope.One, Peers: peers));

    /// <summary>検証用の一NICだけを返します。</summary>
    /// <returns>公開APIと同じ候補JSONです。</returns>
    private static JsonElement Network(string id) => JsonSerializer.SerializeToElement(new[] { new { id, name = "NIC", addresses } });

    /// <summary>HTTP応答は受信側が破棄します。</summary>
    /// <returns>所有権を渡す応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };

    /// <summary>待機せず候補の更新期限を進めます。</summary>
    private sealed class ChoiceClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        /// <summary>試験の観測時刻です。</summary>
        /// <returns>単調に進めた現在時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => now;

        /// <summary>次の取得期限まで進めます。</summary>
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
