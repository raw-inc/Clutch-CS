using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>視覚表示が契約の型・端点・設定高さを偽らないことを確認します。</summary>
public sealed class GraphPresentationTests
{
    /// <summary>構造未確定のJSON入力とBinaryも受けるAnyを区別し、抽出型を断定しません。</summary>
    [Fact]
    public void DeclaredTypesAndExtractionRemainDistinct()
    {
        NodeDescriptor descriptor = Descriptor([new("any"), new("json", EPortValueKind.Json), new("binary", EPortValueKind.Binary)]);
        Assert.Equal("port.type.any", GraphPresentation.Port(descriptor, "any", true).Label);
        Assert.Equal("port.type.json", GraphPresentation.Port(descriptor, "json", true).Label);
        Assert.Equal("port.type.binary", GraphPresentation.Port(descriptor, "binary", true).Label);
        Assert.Equal("unknown", GraphPresentation.Port(descriptor, "missing", true).Key);
        WireDefinition wire = new("from", "binary", "to", "value", "failed", "/field");
        Assert.Equal("any", GraphPresentation.Wire(descriptor, wire).Key);
        Assert.Equal("failed", wire.When);
        Assert.Equal("/field", wire.Path);
        Assert.Equal("binary", GraphPresentation.Wire(descriptor, wire with { Path = null }).Key);
    }

    /// <summary>共通ポートを通常データ値と区別し、Source以外にSource役割を作りません。</summary>
    [Fact]
    public void CommonPortsKeepTheirRuntimeRoles()
    {
        NodeDescriptor descriptor = Descriptor([]);
        Assert.Equal("trigger", GraphPresentation.Port(descriptor, "trigger", false).Key);
        Assert.Equal("result", GraphPresentation.Port(descriptor, "result", true).Key);
        Assert.Equal("unknown", GraphPresentation.Port(descriptor, "source", true).Key);
        Assert.Equal("source", GraphPresentation.Port(descriptor with { Kind = ENodeKind.Source }, "source", true).Key);
        Assert.Equal("transaction", GraphPresentation.Port(descriptor, "fileTransaction", true).Key);
        Assert.Equal("files", GraphPresentation.CategoryKey("File system"));
        Assert.Equal("math", GraphPresentation.CategoryKey("Math"));
        Assert.Equal("Math", GraphPresentation.CategoryName("Math"));
        Assert.Equal("Communication", GraphPresentation.CategoryName("Communication"));
        Assert.Equal("extension", GraphPresentation.CategoryName("extension"));
        Assert.Equal("unknown", GraphPresentation.CategoryKey("extension"));
    }

    /// <summary>詳細を折り畳んでもポート行と線端点の位置を維持し空設定は高さを消費しません。</summary>
    [Fact]
    public void CardHeightAndEndpointsFollowVisibleFields()
    {
        GraphViewOptions view = new();
        NodeDescriptor empty = Descriptor([]);
        double collapsed = GraphPresentation.CardHeight(empty, 2, 3, false, view);
        Assert.Equal(collapsed, GraphPresentation.CardHeight(empty, 2, 3, true, view));
        NodeDescriptor settings = empty with { SettingsSchema = JsonSerializer.Deserialize<JsonElement>("{\"properties\":{\"enabled\":{\"type\":\"boolean\"}}}") };
        Assert.Equal(collapsed + 24 + 20 + 64, GraphPresentation.CardHeight(settings, 2, 3, true, view));
        Assert.Equal(collapsed, GraphPresentation.CardHeight(settings, 2, 3, false, view));
        Assert.Equal(new NodePosition(300, 152), GraphPresentation.Endpoint(new(10, 20), 2, true, view));
        Assert.Equal(new NodePosition(10, 132), GraphPresentation.Endpoint(new(10, 20), 2, false, view with { ShowPortTypes = false }));
    }

    /// <summary>線形状の変更は端点と利用者の実行定義を変えません。</summary>
    [Theory]
    [InlineData(EGraphWireShape.Curve, "M 10 20 C 90 20, 220 140, 300 140")]
    [InlineData(EGraphWireShape.Straight, "M 10 20 L 300 140")]
    [InlineData(EGraphWireShape.Orthogonal, "M 10 20 H 155 V 140 H 300")]
    public void WireShapesPreserveEndpoints(EGraphWireShape shape, string expected)
    {
        Assert.Equal(expected, GraphPresentation.Path(new(10, 20), new(300, 140), shape));
        Assert.True(new GraphViewOptions().AnimateConnections);
    }

    /// <summary>逆向き・同じ高さ・近接・自己接続でも出力口の外へ出てから折り返します。</summary>
    [Theory]
    [InlineData(EGraphWireShape.Curve, 100, 200)]
    [InlineData(EGraphWireShape.Curve, 100, 100)]
    [InlineData(EGraphWireShape.Curve, 500, 100)]
    [InlineData(EGraphWireShape.Straight, 100, 100)]
    [InlineData(EGraphWireShape.Orthogonal, 500, 100)]
    public void BackwardWiresLeaveThePortBeforeTurning(EGraphWireShape shape, double targetX, double targetY)
    {
        string path = GraphPresentation.Path(new(500, 100), new(targetX, targetY), shape, 40);
        string start = shape == EGraphWireShape.Curve ? "M 500 100 H 524 C"
                                                      : "M 500 100 H 532 V 40";
        Assert.StartsWith(start, path, StringComparison.Ordinal);
        Assert.EndsWith(FormattableString.Invariant($"H {targetX}"), path, StringComparison.Ordinal);
        Assert.Contains("40", path, StringComparison.Ordinal);
    }

    /// <summary>下側・上側・重なるカードとキャンバス上端で、近い安全な折返し位置を選びます。</summary>
    [Theory]
    [InlineData(100, 500, 520, 900, 168, 750, 510)]
    [InlineData(520, 900, 100, 500, 588, 168, 510)]
    [InlineData(100, 500, 200, 600, 450, 550, 632)]
    [InlineData(100, 500, 200, 600, 168, 268, 68)]
    [InlineData(10, 410, 20, 420, 78, 88, 452)]
    public void BackwardWiresChooseTheGapOrNearestOuterEdge(double sourceTop, double sourceBottom, double targetTop, double targetBottom,
        double fromY, double toY, double expected)
    {
        double detour = GraphPresentation.DetourY(sourceTop, sourceBottom, targetTop, targetBottom, fromY, toY);
        Assert.Equal(expected, detour);
        Assert.Equal(FormattableString.Invariant($"M 500 {fromY} H 532 V {expected} H 68 V {toY} H 100"),
            GraphPresentation.Path(new(500, fromY), new(100, toY), EGraphWireShape.Orthogonal, detour));
    }

    /// <summary>型宣言だけを持つ最小descriptorです。</summary>
    /// <returns>表示の検証用契約です。</returns>
    private static NodeDescriptor Descriptor(IReadOnlyList<PortDescriptor> outputs) => new("test", "test", "Data", "test", ENodeKind.Action, EExecutionLocation.Independent, [], outputs, [], JsonSerializer.Deserialize<JsonElement>("{\"properties\":{}}"));
}
