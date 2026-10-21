using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>設定形式の一致と動作の互換性を区別し、未対応の契約で実装を呼ばないことを確認します。</summary>
public sealed class BehaviorContractTests
{
    /// <summary>同じ設定スキーマでも互換宣言がなければ、静的な値の構築より前に拒否します。</summary>
    /// <returns>不適合の位置と、実装が呼ばれないことの確認。</returns>
    [Fact]
    public async Task SameSchemaDoesNotImplyBehaviorCompatibilityAsync()
    {
        VersionedValue value = new(false);
        WorkflowFixture fixture = new(value);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition node = new("value", value.Descriptor.Type, JsonSerializer.SerializeToElement(new { }));
        Runtime.Workflows.Compilation.CompilationResult compiled = fixture.Compiler.Compile(new("flow", "legacy", [node], []));
        Assert.Null(compiled.Workflow);
        Assert.Contains(compiled.Issues, issue => string.Equals(issue.Code, "node.contractVersion.unsupported", StringComparison.Ordinal)
            && string.Equals(issue.NodeId, "value", StringComparison.Ordinal) && string.Equals(issue.Path, "contractVersion", StringComparison.Ordinal));
        Assert.Equal(0, value.Calls);
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "flow", "revision", node, new Dictionary<string, NodeValue>(), "a", fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Executor.ExecuteAsync(request).ConfigureAwait(true)).Outcome);
        Assert.Equal(0, value.Calls);
    }

    /// <summary>互換宣言した実装には、利用者が要求した旧契約を静的評価と通常実行の両方で渡します。</summary>
    /// <returns>期待契約を新しい版に勝手に変えない確認。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeclaredCompatibilityPreservesTheRequestedVersionAsync(int expected)
    {
        VersionedValue value = new(true);
        WorkflowFixture fixture = new(value);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition node = new("value", value.Descriptor.Type, JsonSerializer.SerializeToElement(new { }), ContractVersion: expected);
        Runtime.Workflows.Compilation.CompilationResult compiled = fixture.Compiler.Compile(new("flow", "versioned", [node], []));
        Assert.NotNull(compiled.Workflow);
        Assert.Equal(expected, compiled.Workflow.ConfigurationValues![node.Id].Outputs["value"].Json.GetInt32());
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "flow", compiled.Workflow.Revision, node,
            new Dictionary<string, NodeValue>(), "a", fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        NodeResult executed = await fixture.Executor.ExecuteAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, executed.Outcome);
        Assert.Equal(expected, executed.Outputs["value"].Json.GetInt32());
    }

    /// <summary>実装が受け取った期待版を、そのまま観測可能な値へ返します。</summary>
    private sealed class VersionedValue : PureValueNode
    {
        /// <summary>旧契約に対応するかを、設定スキーマとは別に宣言します。</summary>
        internal VersionedValue(bool legacy)
        {
            IReadOnlyList<int> versions = [];
            if (legacy)
            {
                versions = [1];
            }
            Descriptor = NodeSupport.Describe("test.versioned", "契約の確認", "Test", "{}", []) with { ContractVersion = 2, CompatibleContractVersions = versions };
        }

        /// <summary>実装を呼んだ回数。</summary>
        internal int Calls
        {
            get; private set;
        }

        /// <summary>同じ設定スキーマで、動作契約2を宣言するノード。</summary>
        public override NodeDescriptor Descriptor
        {
            get;
        }

        /// <summary>呼出元が要求した版を確認します。</summary>
        /// <returns>実装が受け取った期待版。</returns>
        protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        {
            Calls++;
            return NodeValue.FromObject(NodeBehaviorContracts.Expected(definition));
        }
    }
}
