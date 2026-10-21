using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>図のカード・ポート・接続線に使用する表示座標を計算します。</summary>
public sealed partial class NodeGraph
{
    private double CanvasWidth => Math.Max(1600, Model.Draft.Document.Nodes.Select(node => Position(node).X + CardWidth(node) + 64).DefaultIfEmpty().Max());
    private double CanvasHeight => Math.Max(1200, Model.Draft.Document.Nodes.Select(node =>
    {
        NodeDescriptor? descriptor = inference.EditorDescriptor(node.Id);
        return Position(node).Y + NodeHeight(node, descriptor, Ports(descriptor, false, node.Id).Count, Ports(descriptor, true).Count) + 40;
    }).DefaultIfEmpty().Max());

    /// <summary>ノードの表示設定とポート数からカードの高さを求めます。</summary>
    /// <returns>キャンバス座標におけるカードの高さです。</returns>
    private double CardHeight(NodeDescriptor? descriptor, int inputs, int outputs) => GraphPresentation.CardHeight(descriptor, inputs, outputs, ShowDetails, View);

    /// <summary>コードブロックだけ利用者指定のサイズを持ち、壊れた保存値は既定へ戻します。</summary>
    /// <returns>描画に使用できる有限の寸法です。</returns>
    private NodeSize CodeSize(NodeDefinition node)
    {
        NodeSize? size = resizing && string.Equals(dragging, node.Id, StringComparison.Ordinal) ? sizePreview
                                                                                                : node.Size;
        return new(size is not null && double.IsFinite(size.Width) ? Math.Clamp(size.Width, 360, 1600)
                                                                   : 560,
            size is not null && double.IsFinite(size.Height) ? Math.Clamp(size.Height, 400, 2000)
                                                             : 520);
    }

    private static string CodeBlockType => "value.code";
    private static string PreviewType => "control.preview";
    private static string ProgressType => "control.progress";

    /// <summary>折畳み時は通常カードと同じ幅へ戻します。</summary>
    /// <returns>ノード幅です。</returns>

    private double CardWidth(NodeDefinition node) => ShowDetails && (string.Equals(node.Type, CodeBlockType, StringComparison.Ordinal) || string.Equals(node.Type, ProgressType, StringComparison.Ordinal) || string.Equals(node.Type, PreviewType, StringComparison.Ordinal)) ? CodeSize(node).Width
                                                                                                                                                                                                                                                                               : 290;

    /// <summary>コード欄をノードの拡縮に追従させます。</summary>
    /// <returns>ポートを含むノードの高さです。</returns>
    private double NodeHeight(NodeDefinition node, NodeDescriptor? descriptor, int inputs, int outputs)
    {
        if (ShowDetails && ConcurrencyNodeTypes.IsStart(node.Type))
        {
            return CardHeight(descriptor, inputs, outputs) + 220;
        }
        if (ShowDetails && (string.Equals(node.Type, CodeBlockType, StringComparison.Ordinal) || string.Equals(node.Type, ProgressType, StringComparison.Ordinal) || string.Equals(node.Type, PreviewType, StringComparison.Ordinal)))
        {
            return CodeSize(node).Height;
        }
        return CardHeight(descriptor, inputs, outputs);
    }

    /// <summary>表示幅に合わせて長い文字列を省略します。</summary>
    /// <returns>上限内の文字列、超過時は省略記号を付けた表示です。</returns>
    private static string Short(string value, int length) => value.Length <= length ? value
                                                                                    : value[..(length - 1)] + "…";

    /// <summary>接続元・接続先・ルートと型情報を説明文へまとめます。</summary>
    /// <returns>接続線のツールチップとアクセシビリティに使う説明です。</returns>
    private string WireDescription(WireDefinition wire, GraphPortPresentation port)
    {
        string description = $"{wire.FromNode}.{PortName(wire.FromNode, wire.FromPort, true)} → {wire.ToNode}.{PortName(wire.ToNode, wire.ToPort, false)} · {port.DisplayLabel(Ui)} · {Ui["Route"]} {wire.When}";
        if (wire.Path is not null)
        {
            description += $" · Path {wire.Path}";
        }
        return description;
    }

    /// <summary>選択中の接続に対応するポートかどうかを判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool EndpointHighlighted(string node, string port, bool outbound)
    {
        int? index = Model.SelectedWire;
        if (index is null || index < 0 || Model.Draft.Document.Wires.Count <= index)
        {
            return false;
        }

        WireDefinition? wire = Model.Draft.Document.Wires[index.Value];
        return outbound ? string.Equals(wire.FromNode, node, StringComparison.Ordinal) && string.Equals(wire.FromPort, port, StringComparison.Ordinal)
                        : string.Equals(wire.ToNode, node, StringComparison.Ordinal) && string.Equals(wire.ToPort, port, StringComparison.Ordinal);
    }

    /// <summary>ノードの保存位置または既定位置を取得します。</summary>
    /// <returns>ドラッグ中の仮位置、保存済み位置、または既定配置の座標です。</returns>
    private NodePosition Position(NodeDefinition node)
    {
        if (!resizing && preview is not null && original is not null && dragOrigins.TryGetValue(node.Id, out NodePosition? origin))
        {
            return new(origin.X + preview.X - original.X, origin.Y + preview.Y - original.Y);
        }
        return node.Position ?? new(60, 80);
    }

    /// <summary>ノード定義と接続状態に対応するポート一覧を返します。</summary>
    /// <returns>表示する入力または出力のポート識別子です。</returns>
    private List<string> Ports(NodeDescriptor? descriptor, bool outbound, string? nodeId = null)
    {
        if (portLists.TryGetValue((descriptor, nodeId, outbound), out List<string>? cached))
        {
            return cached;
        }
        if (outbound && descriptor?.IsTerminal == true)
        {
            return [];
        }
        List<string>? ports = (outbound ? descriptor?.Outputs
                                        : descriptor?.Inputs)?.Select(port => port.Name).ToList() ?? [];
        string common = outbound ? "result"
                                 : "trigger";
        if (!ports.Contains(common) && (outbound || descriptor is null || !EventMergePorts.IsNode(descriptor.Type)))
        {
            ports.Add(common);
        }

        if (outbound && descriptor?.Kind == ENodeKind.Source)
        {
            ports.Add("source");
        }

        if (!outbound && nodeId is not null)
        {
            ports.AddRange(GraphSettingPorts.Collapsed(Model.Draft.Document, nodeId, descriptor, ShowDetails));
        }

        return portLists[(descriptor, nodeId, outbound)] = ports.Distinct().ToList();
    }

    /// <summary>ポート識別子を画面の表示名へ変換します。</summary>
    /// <returns>設定入力を含むポートの翻訳済み表示名です。</returns>
    private string PortName(string nodeId, string port, bool outbound)
    {
        if (!portNames.TryGetValue((nodeId, port, outbound), out string? name))
        {
            name = GraphPortNames.Resolve(Model.Draft.Document, Ui, nodeId, port, outbound);
            portNames[(nodeId, port, outbound)] = name;
        }
        return name;
    }

    /// <summary>設定Schemaに含まれるプロパティを列挙します。</summary>
    /// <returns>設定プロパティの列挙子、未定義なら空の列挙子です。</returns>
    private static JsonElement.ObjectEnumerator SettingProperties(NodeDescriptor descriptor) => descriptor.SettingsSchema.ValueKind == System.Text.Json.JsonValueKind.Object && descriptor.SettingsSchema.TryGetProperty(MatchProperties, out JsonElement properties) && properties.ValueKind == System.Text.Json.JsonValueKind.Object ? properties.EnumerateObject()
                                                                                                                                                                                                                                                                                                                                       : [];

    /// <summary>設定入力ポートの縦位置を求めます。</summary>
    /// <returns>カード内の設定ポートのY座標です。</returns>
    private double SettingY(int inputs, int outputs, int index) => GraphSettingPorts.Y(inputs, outputs, index, View);

    /// <summary>固定入力欄に渡すポートの表示情報を組み立てます。</summary>
    /// <returns>設定入力または推測した入力型に対応する表示情報です。</returns>
    private static GraphPortPresentation InputPresentation(GraphTypeInference inference, string id, string port) => inference.Port(id, port, false);

    /// <summary>指定したノードとポートの接続座標を求めます。</summary>
    /// <returns>ポートのキャンバス座標、端点が存在しなければnullです。</returns>
    private NodePosition? Endpoint(string id, string port, bool outbound)
    {
        NodeDefinition? node = inference.Node(id);
        if (node is null)
        {
            return null;
        }

        NodePosition? position = Position(node);
        NodeDescriptor? descriptor = inference.EditorDescriptor(id);
        if (!outbound && ShowDetails && descriptor is not null && SettingPorts.Property(port) is { } property)
        {
            int settingIndex = SettingProperties(descriptor).Select(item => item.Name).ToList().IndexOf(property);
            if (0 <= settingIndex)
            {
                return new(position.X, position.Y + SettingY(Ports(descriptor, false, id).Count, Ports(descriptor, true, id).Count, settingIndex));
            }
        }

        int index = Ports(descriptor, outbound, id).IndexOf(port);
        NodePosition? endpoint = GraphPresentation.Endpoint(position, index, outbound, View);
        return outbound && endpoint is not null ? endpoint with { X = position.X + CardWidth(node) }
                                                : endpoint;
    }

    /// <summary>逆向きの接続は両端のカードの外側で折り返し、出入り口を隠しません。</summary>
    /// <returns>同じ端点と表示形状を維持した経路です。</returns>
    private string WirePath(WireDefinition wire, NodePosition from, NodePosition to)
    {
        double? detour = null;
        if (to.X < from.X + 48 && inference.Node(wire.FromNode) is { } source && inference.Node(wire.ToNode) is { } target)
        {
            NodeDescriptor? sourceType = inference.EditorDescriptor(source.Id), targetType = inference.EditorDescriptor(target.Id);
            double sourceTop = Position(source).Y, targetTop = Position(target).Y;
            double sourceBottom = sourceTop + NodeHeight(source, sourceType, Ports(sourceType, false, source.Id).Count, Ports(sourceType, true).Count);
            double targetBottom = targetTop + NodeHeight(target, targetType, Ports(targetType, false, target.Id).Count, Ports(targetType, true).Count);
            detour = GraphPresentation.DetourY(sourceTop, sourceBottom, targetTop, targetBottom, from.Y, to.Y);
        }
        return GraphPresentation.Path(from, to, View.WireShape, detour);
    }

    /// <summary>ブラウザーの表示座標をキャンバス内の座標へ変換します。</summary>
    /// <returns>倍率と表示位置を反映したキャンバス座標です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task<NodePosition> ClientToGraphAsync(double x, double y) => await Script.InvokeAsync<NodePosition>("contentsBrowser.point", UiCancellation, canvas, x, y).ConfigureAwait(true);
}
