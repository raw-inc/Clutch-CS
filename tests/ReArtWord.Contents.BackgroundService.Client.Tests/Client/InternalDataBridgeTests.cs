using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.ClientHost.Availability;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実ローカルHTTPから切断・内部編集・再接続の限定経路を確認します。</summary>
public sealed class InternalDataBridgeTests
{
    /// <summary>用途方針がオフライン読取りとローカル変更の可否を変えます。</summary>
    /// <returns>実HTTP境界の確認です。</returns>
    [Theory]
    [InlineData(EAvailabilityOffline.Unavailable, false, false)]
    [InlineData(EAvailabilityOffline.LastConfirmedReadOnly, true, false)]
    [InlineData(EAvailabilityOffline.LocalUnconfirmed, true, true)]
    public async Task OfflineModesNeverBecomeNormalNullOrRemoteWritesAsync(EAvailabilityOffline mode, bool canRead, bool canWrite)
    {
        Fixture fixture = await Fixture.CreateAsync(mode).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        InternalDataClientTarget target = fixture.Target();
        _ = await fixture.PostAsync("read", target).ConfigureAwait(true);
        fixture.Remote.Connected = false;
        InternalDataClientView offline = await fixture.PostAsync("read", target).ConfigureAwait(true);
        Assert.False(offline.Online);
        Assert.Equal(canRead, offline.Current.State.CanRead);
        Assert.Equal(canWrite, offline.Current.State.CanWrite);
        Assert.True(offline.Current.State.IsStale);
        InternalDataClientView staged = await fixture.PostAsync("stage", new InternalDataClientChange(target, "one", NodeValue.FromObject(7), Guid.NewGuid())).ConfigureAwait(true);
        Assert.Equal(canWrite, staged.Current.State.IsLocalUnconfirmed);
        Assert.Equal(0, fixture.Remote.Writes);
        if (canWrite)
        {
            fixture.Remote.Connected = true;
            InternalDataClientView merged = await fixture.PostAsync("reconcile", target).ConfigureAwait(true);
            Assert.False(merged.Current.State.IsLocalUnconfirmed);
            Assert.Equal(7, fixture.Remote.Value.Json.GetInt32());
            Assert.Equal(1, fixture.Remote.Writes);
        }
    }

    /// <summary>再読取りで未送信候補の比較元を進めず、更新済み共有値を上書きしません。</summary>
    /// <returns>競合保持の確認です。</returns>
    [Fact]
    public async Task ReconnectedReadKeepsOriginalBaseAndCasConflictAsync()
    {
        Fixture fixture = await Fixture.CreateAsync(EAvailabilityOffline.LocalUnconfirmed).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        InternalDataClientTarget target = fixture.Target();
        _ = await fixture.PostAsync("read", target).ConfigureAwait(true);
        fixture.Remote.Connected = false;
        _ = await fixture.PostAsync("stage", new InternalDataClientChange(target, "one", NodeValue.FromObject(7), Guid.NewGuid())).ConfigureAwait(true);
        fixture.Remote.Value = NodeValue.FromObject(2);
        fixture.Remote.Revision = "other";
        fixture.Remote.Connected = true;
        InternalDataClientView compared = await fixture.PostAsync("read", target).ConfigureAwait(true);
        Assert.Equal("one", compared.Current.State.SavedRevision);
        Assert.Equal(7, compared.Current.Value!.Json.GetInt32());
        Assert.Equal(2, compared.Remote!.Value!.Json.GetInt32());
        InternalDataClientView conflict = await fixture.PostAsync("reconcile", target).ConfigureAwait(true);
        Assert.Equal("Availability.ReconcileConflict", conflict.Current.State.Reason);
        Assert.True(conflict.Current.State.IsLocalUnconfirmed);
        Assert.Equal(2, fixture.Remote.Value.Json.GetInt32());
    }

    /// <summary>元書込みが不明なら再要求せず、新実体へ旧候補も流用しません。</summary>
    /// <returns>不明と実体変更の確認です。</returns>
    [Fact]
    public async Task UnknownWriteAndNewInstanceCannotReplayCandidateAsync()
    {
        Fixture fixture = await Fixture.CreateAsync(EAvailabilityOffline.LocalUnconfirmed).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        InternalDataClientTarget target = fixture.Target();
        _ = await fixture.PostAsync("read", target).ConfigureAwait(true);
        _ = await fixture.PostAsync("stage", new InternalDataClientChange(target, "one", NodeValue.FromObject(7), Guid.NewGuid())).ConfigureAwait(true);
        fixture.Remote.UnknownWrite = true;
        InternalDataClientView unknown = await fixture.PostAsync("reconcile", target).ConfigureAwait(true);
        Assert.Equal("Availability.OriginalWriteUnknown", unknown.Current.State.Reason);
        _ = await fixture.PostAsync("reconcile", target).ConfigureAwait(true);
        Assert.Equal(1, fixture.Remote.Writes);
        fixture.Remote.Instance = Guid.NewGuid();
        using HttpResponseMessage refused = await fixture.Client.PostAsJsonAsync("/client/internal-data/reconcile", target, ContractJson.Options).ConfigureAwait(true);
        Assert.False(refused.IsSuccessStatusCode);
        InternalDataClientView old = await fixture.PostAsync("read", target).ConfigureAwait(true);
        Assert.False(old.Current.State.CanWrite);
        Assert.Equal(target.InstanceId, old.InstanceId);
        Assert.Equal(1, fixture.Remote.Writes);
    }

    /// <summary>K1の未保存現在値をオフライン確認済み保存値へ偽装しません。</summary>
    /// <returns>現在値と保存値の分離確認です。</returns>
    [Fact]
    public async Task UnsavedCurrentNeverBecomesConfirmedOfflineValueAsync()
    {
        Fixture fixture = await Fixture.CreateAsync(EAvailabilityOffline.LastConfirmedReadOnly).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Remote.Unsaved = true;
        InternalDataClientTarget target = fixture.Target();
        InternalDataClientView online = await fixture.PostAsync("read", target).ConfigureAwait(true);
        Assert.True(online.Current.State.IsUnsaved);
        Assert.Equal(99, online.Current.Value!.Json.GetInt32());
        fixture.Remote.Connected = false;
        InternalDataClientView offline = await fixture.PostAsync("read", target).ConfigureAwait(true);
        Assert.Equal(1, offline.Current.Value!.Json.GetInt32());
        Assert.Equal("one", offline.Current.Revision);
    }

    /// <summary>Unknownの元結果だけを照会し、成功完了または確定未実施からの再編集へ進めます。</summary>
    /// <returns>再送しない解除境界です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadonlyReceiptClearsUnknownWithoutResendingAsync(bool applied)
    {
        Fixture fixture = await Fixture.CreateAsync(EAvailabilityOffline.LocalUnconfirmed).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        InternalDataClientTarget target = fixture.Target();
        _ = await fixture.PostAsync("read", target).ConfigureAwait(true);
        _ = await fixture.PostAsync("stage", new InternalDataClientChange(target, "one", NodeValue.FromObject(7), Guid.NewGuid())).ConfigureAwait(true);
        fixture.Remote.UnknownWrite = true;
        _ = await fixture.PostAsync("reconcile", target).ConfigureAwait(true);
        EOutcome confirmed = EOutcome.NotExecuted;
        if (applied)
        {
            fixture.Remote.Value = NodeValue.FromObject(7);
            fixture.Remote.Revision = "confirmed";
            confirmed = EOutcome.Succeeded;
        }
        AvailabilityValue current = new(fixture.Remote.Value, fixture.Remote.Revision, new(EStoragePurpose.Runtime, "state:key", "peer", fixture.Remote.Revision, fixture.Remote.Revision, fixture.Remote.Revision));
        fixture.Remote.QueryResult = new(fixture.Remote.LastOperation, new(confirmed, applied, current));
        InternalDataClientView result = await fixture.PostAsync("query", target).ConfigureAwait(true);
        Assert.Equal(1, fixture.Remote.Writes);
        Assert.Equal(!applied, result.Current.State.IsLocalUnconfirmed);
        if (!applied)
        {
            Assert.True(result.Current.State.CanWrite);
            fixture.Remote.UnknownWrite = false;
            _ = await fixture.PostAsync("stage", new InternalDataClientChange(target, result.Current.Revision, NodeValue.FromObject(8), Guid.NewGuid())).ConfigureAwait(true);
            _ = await fixture.PostAsync("reconcile", target).ConfigureAwait(true);
            Assert.Equal(2, fixture.Remote.Writes);
            Assert.Equal(8, fixture.Remote.Value.Json.GetInt32());
        }
        else
        {
            Assert.Equal(7, result.Confirmed!.Value!.Json.GetInt32());
        }
    }

    /// <summary>実HTTPと一つの有限bridgeを所有する試験用の構成です。</summary>
    private sealed class Fixture(WebApplication app, InternalDataBridge bridge, string directory, RemoteData remote, HttpClient client) : IAsyncDisposable
    {
        internal RemoteData Remote => remote;

        internal HttpClient Client => client;

        /// <summary>固定する実体付きの内部対象です。</summary>
        /// <returns>試験の対象です。</returns>
        internal InternalDataClientTarget Target() => new(new("workflow", EInternalDataScope.Shared, "key"), remote.Instance);

        /// <summary>本番と同じローカル経路を起動します。</summary>
        /// <returns>呼出元が終了する試験用の構成です。</returns>
        // 抑制理由: bridgeとHttpClientの所有権を返却するFixtureへ移し、各試験のawait usingで解放します。
        // 抑制理由: 初期化失敗を維持し、後始末も失敗した場合は両方を試験結果へ返します。
        [SuppressMessage("Reliability", "CA2000", Justification = "作成したbridgeとHttpClientはFixture.DisposeAsyncが所有して解放します。"),
         SuppressMessage("SourceQuality", "RAW330", Justification = "後始末後も初期化失敗の例外と発生位置を維持します。"),
         SuppressMessage("SourceQuality", "RAW333", Justification = "初期化失敗と後始末の失敗を両方保持して試験結果へ返します。")]
        internal static async Task<Fixture> CreateAsync(EAvailabilityOffline mode)
        {
            string directory = Path.Combine(Path.GetTempPath(), "contents-k2-" + Guid.NewGuid().ToString("N"));
            RemoteData remote = new(mode);
            InternalDataBridge? bridge = null;
            WebApplication? app = null;
            HttpClient? client = null;
            try
            {
                bridge = new(remote, directory);
                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton(bridge);
                builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
                app = builder.Build();
                app.Use((Microsoft.AspNetCore.Http.HttpContext context, Microsoft.AspNetCore.Http.RequestDelegate next) => ClientErrors.HandleAsync(context, next, context.RequestAborted));
                InternalDataRoutes.Map(app);
                await app.StartAsync().ConfigureAwait(false);
                client = new() { BaseAddress = new Uri(app.Urls.Single()) };
                client.DefaultRequestHeaders.Add("X-Contents-Cluster", "cluster");
                client.DefaultRequestHeaders.Add("X-Contents-Peer", "peer");
                return new(app, bridge, directory, remote, client);
            }
            catch (Exception startupError)
            {
                try
                {
                    await CleanupAsync(client, app, bridge, directory).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(startupError, cleanupError);
                }
                throw;
            }
        }

        /// <summary>実HTTP応答を共通DTOで読みます。</summary>
        /// <returns>応答した状態です。</returns>
        internal async Task<InternalDataClientView> PostAsync<T>(string operation, T body)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync("/client/internal-data/" + operation, body, ContractJson.Options).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<InternalDataClientView>(ContractJson.Options).ConfigureAwait(false))!;
        }

        /// <summary>実待受と一時保存先を終了します。</summary>
        /// <returns>終了完了です。</returns>
        public ValueTask DisposeAsync() => CleanupAsync(client, app, bridge, directory);

        /// <summary>初期化途中でも待受を終了し、所有する保存領域を回収します。</summary>
        /// <returns>試験専用のHTTP接続と保存資源の解放です。</returns>
        private static async ValueTask CleanupAsync(HttpClient? client, WebApplication? app, InternalDataBridge? bridge, string directory)
        {
            try
            {
                client?.Dispose();
                if (app is not null)
                {
                    try
                    {
                        await app.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await app.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                try
                {
                    bridge?.Dispose();
                }
                finally
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
            }
        }
    }

    /// <summary>通信断と共有側更新だけを制御する内部データ接続です。</summary>
    private sealed class RemoteData(EAvailabilityOffline mode) : IInternalDataRemote
    {
        internal bool Connected
        {
            get; set;
        } = true;

        internal bool UnknownWrite
        {
            get; set;
        }

        internal bool Unsaved
        {
            get; set;
        }

        internal Guid Instance
        {
            get; set;
        } = Guid.NewGuid();

        internal NodeValue Value
        {
            get; set;
        } = NodeValue.FromObject(1);

        internal string Revision
        {
            get; set;
        } = "one";

        internal int Writes
        {
            get; private set;
        }

        internal Guid LastOperation
        {
            get; private set;
        }

        internal InternalDataWriteResult? QueryResult
        {
            get; set;
        }

        /// <summary>通信できる時だけ固定実体を返します。</summary>
        /// <returns>発見した実体です。</returns>
        public PeerInfo? Find(string clusterId, string peerId)
        {
            if (!Connected)
            {
                return null;
            }
            return new("peer", Instance, "host", "127.0.0.1", 1, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        }

        /// <summary>保存値と未保存現在値を意図的に別値にします。</summary>
        /// <returns>確認済み内部値です。</returns>
        public Task<InternalDataReadResult> ReadAsync(string clusterId, PeerInfo peer, InternalDataTarget target, CancellationToken cancellationToken = default)
        {
            NodeValue current = Value;
            if (Unsaved)
            {
                current = NodeValue.FromObject(99);
            }
            AvailabilityState state = new(EStoragePurpose.Runtime, "state:key", "peer", Revision, Revision, Revision, IsUnsaved: Unsaved);
            return Task.FromResult(new InternalDataReadResult(new(current, Revision, state), Value, Revision, true, new(Offline: mode)));
        }

        /// <summary>実行回数を増やさず元の固定結果を返します。</summary>
        /// <returns>試験が確定させた操作記録結果です。</returns>
        public Task<InternalDataWriteResult?> QueryAsync(Guid commandId, Guid operationId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(LastOperation, operationId);
            return Task.FromResult(QueryResult);
        }

        /// <summary>基準版が一致する強いCASだけを適用します。</summary>
        /// <returns>保存結果です。</returns>
        public Task<InternalDataWriteResult> WriteAsync(string clusterId, PeerInfo peer, InternalDataWriteRequest request, bool compareExchange, CancellationToken cancellationToken = default)
        {
            Assert.True(compareExchange);
            Writes++;
            LastOperation = request.OperationId;
            if (UnknownWrite)
            {
                return Task.FromResult(new InternalDataWriteResult(request.OperationId, new(EOutcome.Unknown, false, null)));
            }
            bool applied = string.Equals(Revision, request.ExpectedRevision, StringComparison.Ordinal);
            if (applied)
            {
                Value = request.Value;
                Revision = "updated";
            }
            EOutcome outcome = EOutcome.Failed;
            if (applied)
            {
                outcome = EOutcome.Succeeded;
            }
            return Task.FromResult(new InternalDataWriteResult(request.OperationId, new(outcome, applied, new(Value, Revision, new(EStoragePurpose.Runtime, "state:key", "peer", Revision, Revision, Revision)))));
        }
    }
}
