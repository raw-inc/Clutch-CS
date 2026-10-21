using System.Buffers.Binary;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.ArtNet;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>接続線仕様と不正パケットの境界を検証します。</summary>
public sealed class WireCodecTests
{
    /// <summary>指定精度の範囲外の値を、無限大へ置き換えて送信しません。</summary>
    [Theory]
    [InlineData("f", "1e100")]
    [InlineData("d", "1e400")]
    public void OscRejectsFloatingPointOverflow(string tag, string number)
    {
        using JsonDocument document = JsonDocument.Parse("{\"address\":\"/number\",\"arguments\":[{\"type\":\"" + tag + "\",\"value\":" + number + "}]}");
        Assert.Throws<FormatException>(() => OscPacketCodec.Encode(document.RootElement));
    }

    /// <summary>既知のOSCバイト順とpaddingを確認します。</summary>
    [Fact]
    public void OscKnownMessageUsesNetworkByteOrderAndZeroPadding()
    {
        JsonElement value = JsonSerializer.SerializeToElement(new { address = "/x", arguments = new[] { new { type = "i", value = 0x01020304 } } });
        byte[] packet = OscPacketCodec.Encode(value);
        Assert.Equal(new byte[] { 47, 120, 0, 0, 44, 105, 0, 0, 1, 2, 3, 4 }, packet);
        Assert.Equal(0x01020304, OscPacketCodec.Decode(packet).GetProperty("arguments")[0].GetProperty("value").GetInt32());
        packet[3] = 1;
        Assert.Throws<FormatException>(() => OscPacketCodec.Decode(packet));
    }

    /// <summary>bundle時刻とblob、64bit整数を欠損なく保持します。</summary>
    [Fact]
    public void OscBundlePreservesTypedValuesAndRejectsTruncation()
    {
        using JsonDocument document = JsonDocument.Parse("""{"timetag":123456789012345,"elements":[{"address":"/value","arguments":[{"type":"h","value":9007199254740993},{"type":"b","value":"AAH/"},{"type":"T"}]}]}""");
        byte[] packet = OscPacketCodec.Encode(document.RootElement);
        JsonElement restored = OscPacketCodec.Decode(packet);
        Assert.Equal(123456789012345UL, restored.GetProperty("timetag").GetUInt64());
        Assert.Equal(9007199254740993L, restored.GetProperty("elements")[0].GetProperty("arguments")[0].GetProperty("value").GetInt64());
        Assert.Equal(packet, OscPacketCodec.Encode(restored));
        Assert.Throws<FormatException>(() => OscPacketCodec.Decode(packet.AsSpan(0, packet.Length - 1)));
    }

    /// <summary>ArtDMXの混合バイト順、長さ、Port-Addressを検証します。</summary>
    [Fact]
    public void ArtDmxUsesSpecifiedHeaderAndRejectsMalformedLength()
    {
        JsonElement value = JsonSerializer.SerializeToElement(new { universe = 0x1234, sequence = 7, physical = 1, data = "AH//AA==" });
        byte[] packet = ArtNetDmxCodec.Encode(value);
        Assert.Equal(0x5000, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8)));
        Assert.Equal(14, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(14)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)));
        Assert.Equal("AH//AA==", ArtNetDmxCodec.Decode(packet).GetProperty("data").GetString());
        packet[17] = 3;
        Assert.Throws<FormatException>(() => ArtNetDmxCodec.Decode(packet));
    }
}
