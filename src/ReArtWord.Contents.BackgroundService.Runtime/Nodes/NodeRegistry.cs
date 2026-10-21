using System.Collections.Frozen;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>DIから渡されたノードを種類名で解決します。</summary>
public sealed class NodeRegistry
{
    private readonly FrozenDictionary<string, INode> nodes;
    private readonly IReadOnlyList<NodeDescriptor> descriptors;

    /// <summary>種類の重複と宣言した実行寿命の不一致を拒否して登録します。</summary>
    public NodeRegistry(IEnumerable<INode> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        Dictionary<string, INode> registered = new(StringComparer.Ordinal);
        foreach (INode node in registrations)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (!NodeBehaviorContracts.ValidDeclaration(node.Descriptor))
            {
                throw new ArgumentException("ノードの動作契約または互換版の宣言が不正です。", nameof(registrations));
            }
            if (!registered.TryAdd(node.Descriptor.Type, node))
            {
                throw new ArgumentException("ノードの種類識別子が重複しています。", nameof(registrations));
            }
            bool correctKind = node.Descriptor.Kind switch
            {
                ENodeKind.Action => node is IActionNode,
                ENodeKind.Source => node is ISourceNode,
                _ => false,
            };
            if (!correctKind)
            {
                throw new ArgumentException("ノードの宣言と実行契約が一致しません。", nameof(registrations));
            }
        }
        nodes = registered.ToFrozenDictionary(StringComparer.Ordinal);
        descriptors = Array.AsReadOnly(registered.Values.Where(node => !node.Descriptor.IsHidden).Select(Describe).OrderBy(static item => item.Type, StringComparer.Ordinal).ToArray());
    }

    /// <summary>実装が提供する仮想出力だけをカタログへ公開します。</summary>
    /// <returns>通常宣言に実ファイル補償のJSON出力を加えた情報です。</returns>
    private static NodeDescriptor Describe(INode node)
    {
        string port = "fileTransaction";
        NodeDescriptor descriptor = CompletionPorts.WithOutputs(StartSignalPorts.WithSourceOutput(node.Descriptor))! with { CanEvaluateStatically = node is IValueNode };
        if (node is IValueNode)
        {
            descriptor = descriptor with { IsValueOperation = true };
        }
        else
        {
            descriptor = descriptor with { Inputs = [.. descriptor.Inputs, new(PcSelectionPorts.Execution, EPortValueKind.PcSelection)] };
        }
        if (node is not ICompensableFileNode)
        {
            return descriptor;
        }
        return descriptor with { Outputs = [.. descriptor.Outputs.Where(item => !string.Equals(item.Name, port, StringComparison.Ordinal)), new(port, EPortValueKind.Json)] };
    }

    /// <summary>設定画面と検証で共用するノード一覧です。</summary>
    public IReadOnlyList<NodeDescriptor> Descriptors => Dynamic is null ? descriptors
                                                                        : descriptors.Concat(Dynamic.Nodes.Select(Describe)).ToArray();

    /// <summary>このランタイムが所有するクラスタ内の追加レシピです。</summary>
    public IDynamicNodeCatalog? Dynamic
    {
        get; set;
    }

    /// <summary>登録済みノードを解決します。</summary>
    /// <returns>未登録の種類ならnullです。</returns>
    public INode? Find(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return nodes.GetValueOrDefault(type) ?? Dynamic?.Find(type);
    }
}
