using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>Peerの最終受信からの経過時間をHTTPで引き継ぎ、保存やPeer間通知の形式とは分けます。</summary>
public sealed class PeerObservationTests
{
    /// <summary>閲覧PCとClientHostの日時を補正しても、生存期間には元の最終受信からの経過時間と通信の経過時間を使います。</summary>
    /// <returns>実HTTPと通常のManagementApiによる確認です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task HttpPreservesObservationAgeAcrossIndependentClockCorrectionsAsync(int correction)
    {
        ObservationClock source = new(), browser = new();
        PeerInfo peer = Peer(source);
        DateTimeOffset received = peer.LastSeen;
        source.Advance(TimeSpan.FromSeconds(3));
        source.Adjust(TimeSpan.FromSeconds(correction));
        browser.Adjust(TimeSpan.FromSeconds(-correction));
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new PeerObservationJsonConverter()));
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        ClientHttpPolicy.Configure(app, source);
        app.MapGet("/client/peer", () =>
        {
            browser.Advance(TimeSpan.FromSeconds(2));
            return peer;
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await app.StartAsync(timeout.Token).ConfigureAwait(true);
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };
            ManagementApi api = new(client, new(browser));
            PeerInfo observed = (await api.GetAsync<PeerInfo>("/client/peer", timeout.Token).ConfigureAwait(true))!;
            Assert.Equal(received, observed.LastSeen);
            Assert.Equal(TimeSpan.FromSeconds(5), observed.AgeAt(api.UtcNow));
            browser.Adjust(TimeSpan.FromHours(2));
            source.Adjust(TimeSpan.FromHours(-2));
            browser.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(TimeSpan.FromSeconds(16), observed.AgeAt(api.UtcNow));
            source.Advance(TimeSpan.FromSeconds(11));
            PeerInfo repeated = (await api.GetAsync<PeerInfo>("/client/peer", timeout.Token).ConfigureAwait(true))!;
            Assert.Equal(TimeSpan.FromSeconds(16), repeated.AgeAt(api.UtcNow));
            Assert.Equal(received, repeated.LastSeen);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>生存根拠を永続化せず、要求入力から任意の最終受信からの経過時間を注入できないことを確認します。</summary>
    [Fact]
    public void ObservationAgeIsOnlyWrittenToReadResponses()
    {
        ObservationClock clock = new();
        PeerInfo peer = Peer(clock);
        string persisted = JsonSerializer.Serialize(peer, ContractJson.Options);
        Assert.Null(JsonSerializer.Deserialize<PeerInfo>(persisted, ContractJson.Options)!.ObservationAge);
        JsonSerializerOptions output = new(ContractJson.Options);
        output.Converters.Add(new PeerObservationJsonConverter());
        string response = JsonSerializer.Serialize(peer, output);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PeerInfo>(response, ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PeerInfo>(response, output));
        Assert.Equal(peer.LastSeen, JsonSerializer.Deserialize<PeerInfo>(persisted, output)!.LastSeen);
    }

    /// <summary>未対応Hostの応答は日時による既存判定を維持し、不正な経過時間を新しい観測にはしません。</summary>
    [Fact]
    public void LegacyResponsesAndMalformedAgesHaveExplicitBoundaries()
    {
        ObservationClock clock = new();
        PeerInfo peer = Peer(clock);
        JsonSerializerOptions options = new(ContractJson.Options);
        options.Converters.Add(new PeerObservationJsonConverter(age => new(clock, age)));
        string legacy = JsonSerializer.Serialize(peer, ContractJson.Options);
        PeerInfo loaded = JsonSerializer.Deserialize<PeerInfo>(legacy, options)!;
        Assert.Null(loaded.ObservationAge);
        Assert.Equal(TimeSpan.FromSeconds(7), loaded.AgeAt(peer.LastSeen.AddSeconds(7)));
        foreach (string invalid in new[] { "-1", "null", "1.5", "\"3\"", "9223372036854775808" })
        {
            string malformed = legacy[..^1] + ",\"observationAgeTicks\":" + invalid + "}";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PeerInfo>(malformed, options));
        }
        ObservationAge maximum = new(clock, TimeSpan.MaxValue);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.MaxValue, maximum.Elapsed);
    }

    /// <summary>現在観測したPeerを作ります。</summary>
    /// <returns>表示日時と単調時計の両方を持つ観測です。</returns>
    private static PeerInfo Peer(TimeProvider clock) => new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, clock.GetUtcNow()) { ObservationAge = new(clock) };
}
