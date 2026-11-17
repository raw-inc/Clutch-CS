using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.Tests.Client;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>ブラウザー・ClientHost・Coreの時計差を、手動操作の期限へ持ち込みません。</summary>
public sealed class BrowserCommandClockTests
{
    /// <summary>準備の待ち時間を差し引き、時計補正・台帳再起動後も内側と外側の受付期限を維持します。</summary>
    /// <returns>HTTPの時計取得から、実台帳とCore側の期限検証までの確認です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task BrowserAndHostClockChangesDoNotRenewNodeAdmissionAsync(int browserOffset)
    {
        Clock browser = new(), host = new(), core = new();
        browser.Adjust(TimeSpan.FromSeconds(browserOffset));
        core.Adjust(TimeSpan.FromHours(2));
        ClientRequestClock issuer = new(host);
        using ClientHttpHandler handler = new((request, _) =>
        {
            Assert.Equal("/api/request-clock", request.RequestUri!.AbsolutePath);
            ClientRequestClockSnapshot sample = issuer.Capture();
            browser.Advance(TimeSpan.FromSeconds(2));
            host.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(sample, options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost/") };
        ManagementApi api = new(http, new(browser));
        Guid id = Guid.NewGuid(), instance = Guid.NewGuid();
        ClockedNodeCommand node = await api.PrepareNodeCommandAsync(expires => new(id, id, "manual", "r",
            new("node", "test", JsonSerializer.SerializeToElement(new { })), new Dictionary<string, NodeValue>(), "peer", instance, expires)).ConfigureAwait(true);
        host.Adjust(TimeSpan.FromHours(-3));
        browser.Adjust(TimeSpan.FromHours(4));
        RequestAdmission local = issuer.Accept(node);
        Assert.Equal(TimeSpan.FromSeconds(296), local.Remaining);
        Assert.Throws<InvalidDataException>(() => new ClientRequestClock(host).Accept(node));
        DirectoryInfo directory = Directory.CreateTempSubdirectory("clutch-browser-clock-");
        try
        {
            PeerInfo peer = new("peer", instance, "host", "127.0.0.1", 12345, EPeerStatus.Ready, 1, null, core.GetUtcNow());
            RequestClockClient transport = new(host);
            RequestAdmissionClock receiver = new(instance, core);
            ManagementCommand saved;
            using (ClientCommandLedger ledger = new(directory.FullName, host))
            {
                saved = await ledger.PrepareTimedAsync(id, "cluster", "peer", "nodes.execute", ContractJson.ToValue(node.Request), peer,
                    async (command, token) =>
                    {
                        ClusterRequest request = ManagementWire.Request(command, false) with { Admission = command.Admission };
                        ClockedClusterRequest envelope = (await transport.PrepareAsync(request, (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded,
                            NodeValue.FromObject(receiver.Capture()))), token).ConfigureAwait(false))!;
                        return new RequestClockStamp(envelope.ClockInstanceId, envelope.StartByTicks, envelope.SourceNow);
                    }, local).ConfigureAwait(true);
            }
            ManagementCommand received = receiver.Accept(saved, saved.DeliveryTiming!);
            Assert.Equal(node.Request.ExpiresAt, saved.ExpiresAt);
            Assert.Equal(TimeSpan.FromSeconds(296), received.Admission!.ForDeadline(node.Request.ExpiresAt).Remaining);
            using ClientCommandLedger restarted = new(directory.FullName, host);
            ManagementCommand same = await restarted.PrepareTimedAsync(id, "cluster", "peer", "nodes.execute", ContractJson.ToValue(node.Request), peer,
                (_, _) => throw new InvalidOperationException("A restored command must keep its original clock.")).ConfigureAwait(true);
            Assert.Equal(saved.DeliveryTiming, same.DeliveryTiming);
            Assert.Equal(ContractJson.Hash(saved), ContractJson.Hash(same));
            core.Advance(TimeSpan.FromMinutes(5));
            Assert.True(receiver.Accept(same, same.DeliveryTiming!).Admission!.IsExpired);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>UTCと経過時間を別々に動かし、OSには変更を加えません。</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc = DateTimeOffset.UtcNow;
        private long elapsed;

        /// <summary>TimeSpanと同じ単位で計測します。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>要求元ごとの日時を返します。</summary>
        /// <returns>試験用UTC日時です。</returns>
        public override DateTimeOffset GetUtcNow() => utc;

        /// <summary>日時補正に影響されない時計です。</summary>
        /// <returns>経過tickです。</returns>
        public override long GetTimestamp() => elapsed;

        /// <summary>日時だけを補正します。</summary>
        internal void Adjust(TimeSpan offset) => utc += offset;

        /// <summary>実際の時間経過を与えます。</summary>
        internal void Advance(TimeSpan amount)
        {
            elapsed += amount.Ticks;
            utc += amount;
        }
    }
}
