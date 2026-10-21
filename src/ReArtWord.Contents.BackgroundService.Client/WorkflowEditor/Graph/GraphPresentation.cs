using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>カタログとグラフの分類・型・geometryを一箇所で定義します。</summary>
internal static class GraphPresentation
{
    private static readonly (string Name, string Label, string Key)[] categories =
    [
        ("Communication", "Communication", "communication"), ("Control", "Control", "control"), ("Data", "Data conversion", "data"),
        ("SubWorkflows", "SubWorkflows", "control"),
        ("Values", "Values & types", "values"), ("Math", "Math", "math"), ("State", "State", "state"), ("File system", "Files", "files"), ("Processes", "Processes", "processes"),
        ("Scheduling", "Time and scheduling", "scheduling"), ("Resources", "Resource monitoring", "resources"), ("Execution", "Execution PCs", "execution"),
        ("Media", "Media", "media"), ("AI", "AI", "ai"), ("AudioGeneration", "Audio generation", "audio-generation"),
        ("TextGeneration", "Text generation", "ai"), ("VideoGeneration", "Video generation", "ai"), ("ImageGeneration", "Image generation", "ai"),
        ("Database", "Business databases", "database"), ("Scripting", "Scripts", "scripting"),
    ];

    /// <summary>既知の分類を利用場面の順で並べます。</summary>
    /// <returns>未知分類は末尾の順です。</returns>
    internal static int CategoryOrder(string name)
    {
        int index = Array.FindIndex(categories, item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return index < 0 ? categories.Length
                         : index;
    }

    /// <summary>同じ分類に同じ表示名を使用します。</summary>
    /// <returns>未知分類は元の名前です。</returns>
    internal static string CategoryName(string name) => categories.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal)).Label ?? name;

    /// <summary>任意の分類名をCSSへ直接埋めません。</summary>
    /// <returns>既知分類キーかunknownです。</returns>
    internal static string CategoryKey(string name) => categories.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal)).Key ?? "unknown";

    /// <summary>共通ポートの役割と、設定なしで分かる値の種類を表示します。</summary>
    /// <returns>値型または共通ポートの役割です。</returns>
    internal static GraphPortPresentation Port(NodeDescriptor? descriptor, string name, bool outbound)
    {
        if (string.Equals(name, ConcurrencyNodeTypes.ScopePort, StringComparison.Ordinal) && descriptor is not null
            && (outbound && ConcurrencyNodeTypes.IsStart(descriptor.Type) || !outbound && ConcurrencyNodeTypes.IsEnd(descriptor.Type)))
        {
            return new(name, "loop", "port.type.concurrencyScope", "port.description.concurrencyScope");
        }
        if (string.Equals(name, IterationNodeTypes.LoopPort, StringComparison.Ordinal)
            && descriptor is not null && ((outbound && IterationNodeTypes.IsStart(descriptor.Type)) || (!outbound && IterationNodeTypes.IsEnd(descriptor.Type))))
        {
            return new(name, "loop", "port.type.loop", "port.description.loop");
        }
        string trigger = "trigger";
        bool startSignal = outbound && StartSignalPorts.IsOutput(descriptor, name);
        string result = "result";
        string source = "source";
        string transaction = "fileTransaction";
        if (startSignal && descriptor?.Kind == ENodeKind.Source)
        {
            return new(name, "trigger", "port.type.trigger", "port.description.sourceStart");
        }
        if (startSignal || (!outbound && (string.Equals(name, trigger, StringComparison.Ordinal)
            || descriptor is not null && (SignalMergePorts.IsNode(descriptor.Type) && 0 < SignalMergePorts.Index(name)
                || IterationNodeTypes.IsEnd(descriptor.Type) && IterationNodeTypes.IsControlPort(name)))))
        {
            return new(name, "trigger", "port.type.trigger", "port.description.trigger");
        }
        if (outbound && string.Equals(name, result, StringComparison.Ordinal))
        {
            return new(name, "result", "port.type.result", "port.description.result");
        }
        if (outbound && descriptor?.Kind == ENodeKind.Source && string.Equals(name, source, StringComparison.Ordinal))
        {
            return new(name, "source", "port.type.source", "port.description.source");
        }
        if (outbound && string.Equals(name, transaction, StringComparison.Ordinal))
        {
            return new(name, "transaction", "port.type.transaction", "port.description.transaction");
        }
        IReadOnlyList<PortDescriptor>? declared = descriptor?.Inputs;
        if (outbound)
        {
            declared = descriptor?.Outputs;
        }
        PortDescriptor? port = declared?.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        if (port?.IsLegacy == true)
        {
            string explanation = "This existing input only keeps the start dependency; its value is not used. Use the trigger input for new connections.";
            if (outbound)
            {
                explanation = "This output is not produced by this operation.";
            }
            return new(name, "unused", "port.type.unused", explanation);
        }
        GraphPortPresentation presentation = port?.ValueKind switch
        {
            EPortValueKind.Any => new(name, "any", "port.type.any", "port.description.any"),
            EPortValueKind.Json => new(name, "json", "port.type.json", "port.description.json"),
            EPortValueKind.Binary => new(name, "binary", "port.type.binary", "port.description.binary"),
            EPortValueKind.PcSelection => new(name, "pc-selection", "port.type.pc-selection", "port.description.pc-selection"),
            _ => new(name, "unknown", "port.type.unknown", "port.description.unknown"),
        };
        if (outbound && descriptor is not null && port is not null)
        {
            string value = "value";
            if (Contracts.Scripting.ScriptSettings.IsScript(descriptor.Type) && string.Equals(name, value, StringComparison.Ordinal))
            {
                presentation = new(name, "any", "port.type.any", "Script return types are inferred from code. Unconfirmed types are shown as any.");
            }
            return presentation.WithType(BuiltinOutputTypes.Get(descriptor.Type, name));
        }
        if (!outbound && descriptor is not null && port is not null)
        {
            return presentation.WithType(BuiltinInputTypes.Expected(descriptor.Type, name));
        }
        return presentation;
    }

    /// <summary>抽出指定のある線を元のポート型と断定しません。</summary>
    /// <returns>接続表示の型です。</returns>
    internal static GraphPortPresentation Wire(NodeDescriptor? descriptor, WireDefinition wire)
    {
        return wire.Path is null ? Port(descriptor, wire.FromPort, true)
                                 : new(wire.FromPort, "any", "port.type.unknown", "port.description.extracted");
    }

    /// <summary>型labelを表示する場合だけ行を広げます。</summary>
    /// <returns>ポート中心間隔です。</returns>
    internal static int PortSpacing(GraphViewOptions view) => view.ShowPortTypes ? 32
                                                                                 : 22;

    /// <summary>スキーマ表示と同じpropertiesだけを数えます。</summary>
    /// <returns>inline設定数です。</returns>
    internal static int SettingsCount(NodeDescriptor? descriptor)
    {
        string key = "properties";
        return descriptor?.SettingsSchema.ValueKind == JsonValueKind.Object && descriptor.SettingsSchema.TryGetProperty(key, out JsonElement properties) && properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Count()
                                                                                                                                                                                                         : 0;
    }

    /// <summary>空の設定欄を作らずHTML編集欄と外枠の高さを合わせます。</summary>
    /// <returns>card全体の高さです。</returns>
    internal static double CardHeight(NodeDescriptor? descriptor, int inputs, int outputs, bool details, GraphViewOptions view)
    {
        int height = 90 + Math.Max(inputs, outputs) * PortSpacing(view);
        string buttonType = "control.button", toggleType = "control.toggleButton";
        if (string.Equals(descriptor?.Type, buttonType, StringComparison.Ordinal) || string.Equals(descriptor?.Type, toggleType, StringComparison.Ordinal))
        {
            height += 56;
        }
        int settings = SettingsCount(descriptor);
        int constants = descriptor?.Inputs.Count ?? 0;
        if (!details || settings + constants == 0)
        {
            return height;
        }
        if (settings != 0)
        {
            height += 20 + settings * 64;
        }
        if (constants != 0)
        {
            height += 20 + constants * 64;
        }
        return height + 24;
    }

    /// <summary>cardのポート中心と接続線端点を同じ座標で求めます。</summary>
    /// <returns>表示上の端点です。</returns>
    internal static NodePosition Endpoint(NodePosition position, int index, bool outbound, GraphViewOptions view)
    {
        int offset = 0;
        if (outbound)
        {
            offset = 290;
        }
        return new(position.X + offset, position.Y + 68 + Math.Max(0, index) * PortSpacing(view));
    }

    /// <summary>上下に離れたカードはその間を通し、重なる場合だけ近い外周へ迂回します。</summary>
    /// <returns>端点のカード内部を横切らない折返し位置です。</returns>
    internal static double DetourY(double sourceTop, double sourceBottom, double targetTop, double targetBottom, double fromY, double toY)
    {
        if (sourceBottom < targetTop)
        {
            return (sourceBottom + targetTop) / 2;
        }
        if (targetBottom < sourceTop)
        {
            return (targetBottom + sourceTop) / 2;
        }
        double top = Math.Min(sourceTop, targetTop) - 32;
        double bottom = Math.Max(sourceBottom, targetBottom) + 32;
        double topDistance = Math.Abs(fromY - top) + Math.Abs(toY - top);
        double bottomDistance = Math.Abs(fromY - bottom) + Math.Abs(toY - bottom);
        return 0 <= top && topDistance <= bottomDistance ? top
                                                         : bottom;
    }

    /// <summary>同じ端点を保ちながら線形状だけを変えます。</summary>
    /// <returns>cultureに依存しないSVG パスです。</returns>
    internal static string Path(NodePosition from, NodePosition to, EGraphWireShape shape, double? detourY = null)
    {
        if (to.X < from.X + 48)
        {
            double outside = detourY ?? Math.Min(from.Y, to.Y) - 48;
            if (shape == EGraphWireShape.Curve)
            {
                return FormattableString.Invariant($"M {from.X} {from.Y} H {from.X + 24} C {from.X + 48} {from.Y}, {from.X + 48} {outside}, {from.X + 24} {outside} H {to.X - 24} C {to.X - 48} {outside}, {to.X - 48} {to.Y}, {to.X - 24} {to.Y} H {to.X}");
            }
            return FormattableString.Invariant($"M {from.X} {from.Y} H {from.X + 32} V {outside} H {to.X - 32} V {to.Y} H {to.X}");
        }
        double middle = (from.X + to.X) / 2;
        return shape switch
        {
            EGraphWireShape.Straight => FormattableString.Invariant($"M {from.X} {from.Y} L {to.X} {to.Y}"),
            EGraphWireShape.Orthogonal => FormattableString.Invariant($"M {from.X} {from.Y} H {middle} V {to.Y} H {to.X}"),
            _ => FormattableString.Invariant($"M {from.X} {from.Y} C {from.X + 80} {from.Y}, {to.X - 80} {to.Y}, {to.X} {to.Y}"),
        };
    }
}
