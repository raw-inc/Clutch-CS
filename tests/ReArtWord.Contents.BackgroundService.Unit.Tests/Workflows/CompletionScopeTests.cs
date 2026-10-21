using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Nodes.Communication;
using ReArtWord.Contents.BackgroundService.Nodes.Comparison;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.Logic;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>表示・保存検証で同じ非同期完了能力を使い、同期判定へ信号を追加しません。</summary>
public sealed class CompletionScopeTests
{
    /// <summary>値計算・判定・パススルーには共通完了出力を公開しません。</summary>
    [Fact]
    public void SynchronousFamiliesDoNotPublishCompletionPorts()
    {
        NodeRegistry registry = new([new IfNode(), new IfNode(true), new ConditionNode(), new PassNode(), new Nodes.Communication.Osc.OscPacketNode(),
            .. ValuesNodeRegistration.Create(), .. DataNodeRegistration.Create(), .. ArithmeticNodeRegistration.Create(),
            .. ComparisonNodeRegistration.Create(), .. LogicNodeRegistration.Create()]);
        foreach (NodeDescriptor descriptor in registry.Descriptors)
        {
            Assert.False(descriptor.HasAsyncCompletion);
            Assert.DoesNotContain(descriptor.Outputs, port => CompletionPorts.IsPort(port.Name));
            NodeDescriptor configured = ConfiguredNodePorts.Resolve(descriptor, WorkflowFixture.Definition("node", descriptor.Type))!;
            Assert.DoesNotContain(configured.Outputs, port => CompletionPorts.IsPort(port.Name));
        }
        Assert.Contains(registry.Descriptors.Single(node => node.Type is "control.ifStart").Outputs, port => port.Name is "start");
        Assert.Contains(registry.Descriptors.Single(node => node.Type is "control.ifNotStart").Outputs, port => port.Name is "start");
    }

    /// <summary>I/O・待機・反復の完了は保持し、待受開始と混同しません。</summary>
    [Fact]
    public void WaitingOperationsRetainTheirCompletionContract()
    {
        NodeRegistry registry = new([new DelayNode(), new ForEachNode(), new ForEachNode(true), new ForEachEndNode(),
            .. CommunicationNodeRegistration.Create(new TestSecretStore()), .. FileSystemNodeRegistration.Create(new FileSystemOptions([Path.GetTempPath()]))]);
        foreach (NodeDescriptor descriptor in registry.Descriptors)
        {
            NodeDescriptor received = ContractJson.Copy(descriptor);
            bool expected = received.Kind == ENodeKind.Action && !received.IsValueOperation;
            Assert.Equal(expected, CompletionPorts.Supports(received));
            Assert.Equal(expected, received.Outputs.Any(port => string.Equals(port.Name, CompletionPorts.Signal, StringComparison.Ordinal)));
            Assert.Equal(expected, received.Outputs.Any(port => string.Equals(port.Name, CompletionPorts.Success, StringComparison.Ordinal)));
        }
    }

    /// <summary>旧表示で付いた余分な共通出力も、同期ノードの再解決で外します。</summary>
    [Fact]
    public void StaleCompletionPortsAreRemovedWithoutRemovingTheIfSignal()
    {
        NodeDescriptor old = new IfNode().Descriptor;
        old = old with { Outputs = [.. old.Outputs, new(CompletionPorts.Signal), new(CompletionPorts.Success)] };
        NodeDescriptor current = CompletionPorts.WithOutputs(old)!;
        Assert.Equal("start", Assert.Single(current.Outputs).Name);
        Assert.Same(current, CompletionPorts.WithOutputs(current));
    }

    /// <summary>同期ノードの存在しない完了口を保存時に黙って許可しません。</summary>
    [Fact]
    public void SynchronousCompletionWiresAreRejected()
    {
        NodeRegistry registry = new([new IfNode(), new PassNode()]);
        WorkflowDefinition definition = new("invalid-completion", "Invalid", [WorkflowFixture.Definition("if", "control.ifStart"), WorkflowFixture.Definition("sink", "control.pass")],
            [new("if", CompletionPorts.Signal, "sink", "trigger")]);
        CompilationResult result = new WorkflowCompiler(registry).Compile(definition);
        Assert.Null(result.Workflow);
        Assert.NotEmpty(result.Issues);
    }
}
