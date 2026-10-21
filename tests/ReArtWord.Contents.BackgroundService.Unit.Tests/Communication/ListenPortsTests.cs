using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>待受ポートの入力形式と展開上限をソケット確保前に検証します。</summary>
public sealed class ListenPortsTests
{
    private static readonly int[] selectedPorts = [9000, 9002];

    /// <summary>整数・整数配列・列挙と範囲を同じポート集合へ正規化します。</summary>
    [Theory]
    [InlineData("9000", "9000")]
    [InlineData("[9002,9000,9002]", "9000,9002")]
    [InlineData("\"9000-9005\"", "9000,9001,9002,9003,9004,9005")]
    [InlineData("\"9000, 9002, 9006-9010\"", "9000,9002,9006,9007,9008,9009,9010")]
    [InlineData("\" 9002 - 9004,9000,9003 \"", "9000,9002,9003,9004")]
    [InlineData("\"65534-65535\"", "65534,65535")]
    [InlineData("0", "0")]
    [InlineData("\"0\"", "0")]
    [InlineData("[0]", "0")]
    public void ValidFormsHaveTheSameMeaning(string json, string expected)
    {
        IReadOnlyList<int> ports = ListenPorts.Read(JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Equal(expected, string.Join(',', ports));
    }

    /// <summary>不正指定を一部だけ採用したり、動的ポート0を固定ポートへ混ぜたりしません。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("9000.5")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    [InlineData("[]")]
    [InlineData("[9000,\"9001\"]")]
    [InlineData("[9000,null]")]
    [InlineData("[9000,9001.5]")]
    [InlineData("[0,9000]")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"9000,\"")]
    [InlineData("\"9000,,9001\"")]
    [InlineData("\"9002-9000\"")]
    [InlineData("\"9000-9001-9002\"")]
    [InlineData("\"0-1\"")]
    [InlineData("\"0,9000\"")]
    [InlineData("\"1-65535\"")]
    [InlineData("\"+9000\"")]
    [InlineData("\"9e3\"")]
    public void InvalidFormsFailBeforeOpeningAnything(string json)
        => Assert.Throws<FormatException>(() => ListenPorts.Read(JsonSerializer.Deserialize<JsonElement>(json)));

    /// <summary>省略時の互換性を維持し、複数項目を合算した上限も確認します。</summary>
    [Fact]
    public void DefaultAndAggregateLimitAreExplicit()
    {
        Assert.Equal(0, Assert.Single(ListenPorts.Read(default)));
        Assert.Equal(ListenPorts.MaximumCount, ListenPorts.Read(JsonSerializer.SerializeToElement("1-256")).Count);
        Assert.Equal(9000, Assert.Single(ListenPorts.Read(JsonSerializer.SerializeToElement(Enumerable.Repeat(9000, 257).ToArray()))));
        Assert.Throws<FormatException>(() => ListenPorts.Read(JsonSerializer.SerializeToElement("1-128,256-384")));
        Assert.Throws<FormatException>(() => ListenPorts.Read(JsonSerializer.SerializeToElement(Enumerable.Range(1, 257).ToArray())));
    }

    /// <summary>全通信待受で同じ設定契約を公開し、ノード固有検証でも不正範囲を拒否します。</summary>
    [Theory]
    [InlineData("udp.receive")]
    [InlineData("osc.udp.receive")]
    [InlineData("artnet.receive")]
    [InlineData("tcp.listen")]
    [InlineData("osc.tcp.listen")]
    [InlineData("http.listen")]
    [InlineData("websocket.listen")]
    public void AllListenersValidatePortSelections(string type)
    {
        Runtime.Nodes.INode node = SocketNodeTests.Find(type);
        foreach (JsonElement localPort in new[] { JsonSerializer.SerializeToElement(9000), JsonSerializer.SerializeToElement(selectedPorts), JsonSerializer.SerializeToElement("9000,9002-9004") })
        {
            Assert.Empty(node.Validate(new("listener", type, SocketNodeTests.Settings(new { localPort }))));
        }
        Assert.Contains(node.Validate(new("listener", type, SocketNodeTests.Settings(new { localPort = "9005-9000" }))), issue => issue.Code is "network.localPort.invalid");
    }
}
