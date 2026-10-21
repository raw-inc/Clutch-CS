using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>四つの宛先と三種類のキューを、ボタン・値・Signal・レシピで構成します。</summary>
internal static class ButtonBranchWorkflow
{
    internal static readonly int[] Zones = [0, 2, 3, 5];
    internal static readonly string[] Cues = ["standby", "start", "stop"];

    /// <summary>公開入力を独立した送信枝へ接続します。</summary>
    /// <returns>実UDPの宛先以外は運用と同じ構造のレシピです。</returns>
    internal static WorkflowDefinition Recipe(string nic, IReadOnlyDictionary<int, int> ports)
    {
        List<NodeDefinition> nodes = [Field("address", "string"), Field("packet", "object")];
        List<WireDefinition> wires = [];
        foreach (int zone in Zones)
        {
            string id = "zone" + zone.ToString(CultureInfo.InvariantCulture);
            nodes.Add(Field(id, "any"));
            nodes.Add(Node("send-" + id, "osc.udp.send", new { interfaceId = nic, localAddress = "127.0.0.1", localPort = 0, remoteAddress = "127.0.0.1", remotePort = ports[zone] }) with { Target = new(EExecutionScope.One, ["b"]) });
            wires.AddRange([new(id, "value", "send-" + id, "trigger"), new("address", "value", "send-" + id, "address"), new("packet", "value", "send-" + id, "value")]);
        }
        return new("cue-sender", "Cue sender", nodes, wires);
    }

    /// <summary>パケットとアドレスを別の値合流へ、開始Signalを別経路へ配線します。</summary>
    /// <returns>十二個の独立したボタンから一件ずつ送信する親定義です。</returns>
    internal static WorkflowDefinition Definition()
    {
        List<NodeDefinition> nodes = [Node("send", SubworkflowInterface.CallType("cue-sender")), Node("addresses", ValueMergePorts.NodeType), Node("packets", ValueMergePorts.NodeType)];
        List<WireDefinition> wires = [new("addresses", "value", "send", "address"), new("packets", "value", "send", "packet")];
        foreach (int zone in Zones)
        {
            string id = "zone" + zone.ToString(CultureInfo.InvariantCulture);
            nodes.Add(Node(id, "control.signal.first", new { scope = "execution" }));
            wires.Add(new(id, "start", "send", id));
        }
        for (int cueIndex = 0; cueIndex < Cues.Length; cueIndex++)
        {
            string cue = Cues[cueIndex];
            nodes.AddRange([Node(cue, ValueMergePorts.NodeType), Node(cue + "-convert", "data.convert", new { targetType = "string" }),
                Node(cue + "-address", "data.concat") with { Inputs = new Dictionary<string, NodeValue> { ["input1"] = NodeValue.FromObject("/zone/"), ["input3"] = NodeValue.FromObject("/cue/" + cue) } },
                Node(cue + "-packet", "osc.packet") with { Inputs = new Dictionary<string, NodeValue> { ["input1"] = NodeValue.FromObject("slot"), ["input2"] = NodeValue.FromObject(cue) } }]);
            wires.AddRange([new(cue, "value", cue + "-convert", "value"), new(cue + "-convert", "value", cue + "-address", "input2"),
                new(cue + "-address", "value", cue + "-packet", "address"), new(cue + "-address", "value", cue + "-packet", "trigger"),
                new(cue + "-address", "value", "addresses", ConcatPorts.Name(cueIndex + 1)), new(cue + "-packet", "value", "packets", ConcatPorts.Name(cueIndex + 1))]);
            for (int zoneIndex = 0; zoneIndex < Zones.Length; zoneIndex++)
            {
                int zone = Zones[zoneIndex];
                string id = Button(zone, cue), signal = "zone" + zone.ToString(CultureInfo.InvariantCulture);
                nodes.Add(Node(id, ButtonValuePorts.NodeType) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(zone), ["label"] = NodeValue.FromObject(id) }, Target = new(EExecutionScope.One, ["b"]) });
                wires.AddRange([new(id, "value", cue, ConcatPorts.Name(zoneIndex + 1)), new(id, "start", signal, ConcatPorts.Name(cueIndex + 1))]);
            }
        }
        return new("button-cues", "Button cues", nodes, wires);
    }

    /// <summary>押下入口を安定した識別子へ変換します。</summary>
    /// <returns>ゾーン番号とキュー名を持つIDです。</returns>
    internal static string Button(int zone, string cue) => "button-" + zone.ToString(CultureInfo.InvariantCulture) + "-" + cue;

    /// <summary>配線されなかった別宛先をデフォルト値で発火させません。</summary>
    /// <returns>必須の公開入力です。</returns>
    private static NodeDefinition Field(string name, string type) => Node(name, SubworkflowInterface.InputType, new { name, valueType = new { type }, hasDefault = false });

    /// <summary>外部環境を含まない標準設定です。</summary>
    /// <returns>保存して実行するノードです。</returns>
    private static NodeDefinition Node(string id, string type, object? settings = null) => new(id, type, JsonSerializer.SerializeToElement(settings ?? new { }));
}
