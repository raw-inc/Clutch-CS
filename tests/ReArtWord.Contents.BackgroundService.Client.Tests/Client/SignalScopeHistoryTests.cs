using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>外向け配送量、原本寿命と取得側検索の境界を検証します。</summary>
public sealed class SignalScopeHistoryTests
{
    /// <summary>原本9000bytesを一万件受けても、一覧へ混ぜず有限量だけ保持します。</summary>
    [Fact]
    public void SummariesExcludeContentsAndRawRetentionRollsByMemory()
    {
        SignalScopeHistory history = new();
        history.Configure(new("configure", MaximumCount: 100, MaximumBytes: 65536));
        byte[] frame = Packet(new byte[9000], new string('x', 12000));
        for (int index = 0; index < 10000; index++)
        {
            byte[] summary = Assert.IsType<byte[]>(history.Add(frame));
            Assert.True(summary.Length < 1000);
        }
        SignalScopeSearchSnapshot snapshot = history.BeginSearch(new("search", RequestId: 1));
        long position = snapshot.FirstId - 1;
        Assert.InRange(history.Batch(snapshot, ref position).Length, 1, 2);
        Assert.True(Removed(history.Detail(new("detail", PacketId: 1, RequestId: 1))));
        Assert.Equal(3, history.Detail(new("detail", PacketId: 10000, RequestId: 2))[0]);
        history.Clear();
        Assert.True(Removed(history.Detail(new("detail", PacketId: 10000, RequestId: 3))));
        byte[] next = Assert.IsType<byte[]>(history.Add(frame));
        using JsonDocument json = Metadata(next);
        Assert.Equal(10001, json.RootElement.GetProperty("packetId").GetInt64());
    }

    /// <summary>通常行に含まれない本文・Hex・名前を、Advancedだけで照合できます。</summary>
    [Fact]
    public void AdvancedSearchUsesDetailsUtf8AndExactHexWithoutSendingRaw()
    {
        SignalScopeHistory history = new();
        history.Configure(new("configure", HostName: "observing-host", Names: [new("192.0.2.1", "source-host")]));
        byte[] summary = Assert.IsType<byte[]>(history.Add(Packet(Encoding.UTF8.GetBytes("こんにちは raw-value"), "private-detail")));
        using JsonDocument json = Metadata(summary);
        Assert.False(json.RootElement.TryGetProperty("details", out _));
        SignalScopeExpression expression = new(Op: "and", Left: new(Term: "こんにちは"), Right: new(Op: "and", Left: new(Term: "PRIVATE-DETAIL"), Right: new(Term: "source-host")));
        SignalScopeSearchSnapshot snapshot = history.BeginSearch(new("search", RequestId: 1, Expression: expression));
        long position = snapshot.FirstId - 1;
        SignalScopePacket captured = Assert.Single(history.Batch(snapshot, ref position));
        Assert.True(snapshot.Query.Matches(captured, snapshot.HostName, snapshot.Names));
        Assert.True(new SignalScopeQuery(new(Term: "72 61 77 2D"), "hex").Matches(captured, string.Empty, snapshot.Names));
        Assert.False(new SignalScopeQuery(new(Term: "FF FF 00"), "hex").Matches(captured, string.Empty, snapshot.Names));
        Assert.Throws<ArgumentException>(() => new SignalScopeQuery(new(Term: "F"), "hex"));
        Assert.Throws<ArgumentException>(() => history.Configure(new("configure", MaximumCount: 0)));
        Assert.Equal(3, history.Detail(new("detail", PacketId: 1, RequestId: 1))[0]);
    }

    /// <summary>除外経路を更新すると、過去の該当原本にもアクセスできません。</summary>
    [Fact]
    public void UpdatedTransportExclusionsRemoveRetainedContents()
    {
        SignalScopeHistory history = new();
        Assert.NotNull(history.Add(Packet([0, 1, 2], "detail")));
        history.Exclude(JsonSerializer.SerializeToElement(new { excludedEndpoints = new[] { new { transport = "udp", address = "192.0.2.1", port = 7000 } } }));
        Assert.True(Removed(history.Detail(new("detail", PacketId: 1, RequestId: 1))));
        Assert.Null(history.Add(Packet([0, 1, 2], "detail")));
    }

    /// <summary>低速ブラウザーへは最新の有限通知だけを残します。</summary>
    [Fact]
    public void UnreadBrowserNotificationsCannotGrowWithoutBound()
    {
        SignalScopeDelivery delivery = new();
        for (int index = 0; index < 10000; index++)
        {
            delivery.Row(new byte[512]);
        }
        Assert.Equal(10000 - 256, delivery.Skipped);
        delivery.ClearRows();
        delivery.Row([2, 0, 0, 0, 2, 123, 125]);
        Assert.Equal(10000 - 256, delivery.Skipped);
    }

    /// <summary>ネイティブ取得側と同じ原本付きのフレームを作ります。</summary>
    /// <returns>kind 2とJSON長、JSON、rawです。</returns>
    private static byte[] Packet(byte[] raw, string details)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { sourceId = "pcap:test", observationPoint = "rx", time = "2026-10-07T00:00:00Z", linkType = 1,
            originalLength = raw.Length, source = "192.0.2.1", destination = "192.0.2.255", sourcePort = 7000, destinationPort = 7252, protocol = "OSC", transport = "UDP",
            summary = "/address/to", fingerprint = new string('a', 64), details });
        byte[] frame = new byte[5 + json.Length + raw.Length];
        frame[0] = 2;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), json.Length);
        json.CopyTo(frame, 5);
        raw.CopyTo(frame, 5 + json.Length);
        return frame;
    }

    /// <summary>一件のメタデータだけを独立して検証します。</summary>
    /// <returns>呼び出し側で破棄するJSONです。</returns>
    private static JsonDocument Metadata(byte[] frame) => JsonDocument.Parse(frame.AsMemory(5, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1))));

    /// <summary>期限切れ応答を確認します。</summary>
    /// <returns>原本を含まない期限切れかです。</returns>
    private static bool Removed(byte[] frame)
    {
        Assert.Equal(1, frame[0]);
        using JsonDocument json = JsonDocument.Parse(frame.AsMemory(1));
        return json.RootElement.GetProperty("removed").GetBoolean();
    }
}
