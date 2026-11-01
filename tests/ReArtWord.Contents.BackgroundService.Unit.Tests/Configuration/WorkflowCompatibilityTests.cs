using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>互換性の指摘から、保存版・有効版・設定・スナップショットの影響を特定できることを確認します。</summary>
public sealed class WorkflowCompatibilityTests
{
    /// <summary>未対応契約の位置を値の変更や実行なしに返し、設定値を検査結果へ複製しません。</summary>
    /// <returns>実保存とスナップショットを使う影響確認。</returns>
    [Fact]
    public async Task InspectionIdentifiesStoredEffectiveAndSnapshotImpactWithoutMutationAsync()
    {
        using MediaTestFiles files = new();
        string root = files.Root;
        SqliteRepository repository = new(Path.Combine(root, "state.db"));
        PeerCluster cluster = Create(repository, root);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        ReviewNode original = new(1);
        WorkflowCompiler oldCompiler = new(new NodeRegistry([original]));
        WorkflowDefinition definition = new("workflow", "Legacy behavior", [new("node", "test.review", JsonSerializer.SerializeToElement(new { secretRef = "private-reference-123", threshold = 4 }))], []);
        CompiledWorkflow oldPlan = oldCompiler.Compile(definition).Workflow!;
        WorkflowStore workflows = new(repository, cluster, oldCompiler)
        {
            GetEffective = _ => oldPlan,
            ActivateHandler = (_, _) => Task.FromResult(new ConfigurationPhaseResult(Contracts.Execution.EOutcome.Succeeded)),
        };
        WorkflowSaveResult saved = await workflows.SaveAsync(new(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(Contracts.Execution.EOutcome.Succeeded, saved.Saved.Outcome);
        ReviewNode current = new(2);
        NodeRegistry registry = new([current]);
        WorkflowCompatibilityInspection inspection = new(workflows, new(repository, cluster), new(registry), registry, _ => oldPlan, "peer");
        WorkflowCompatibilityReport result = await inspection.InspectAsync(new("workflow")).ConfigureAwait(true);
        Assert.Contains(result.Issues, issue => string.Equals(issue.Area, "saved", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => string.Equals(issue.Area, "effective", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.SnapshotId is not null);
        Assert.All(result.Issues, issue =>
        {
            Assert.Equal("node", issue.NodeId);
            Assert.Equal(1, issue.ExpectedVersion);
            Assert.Equal(2, issue.ImplementationVersion);
            Assert.Contains("threshold", issue.SettingNames);
        });
        Assert.DoesNotContain("private-reference-123", JsonSerializer.Serialize(result, ContractJson.Options), StringComparison.Ordinal);
        Assert.Equal(saved.Revision, (await workflows.GetAsync("workflow").ConfigureAwait(true))!.Revision);
        Assert.Equal(0, original.Executions);
        Assert.Equal(0, current.Executions);
    }

    /// <summary>他試験と独立したローカルPeerを起動します。</summary>
    /// <returns>実保存と実通信を持つ試験Peer。</returns>
    private static PeerCluster Create(IDurableRepository repository, string root)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return new(new(Guid.NewGuid().ToString("N"), "peer", "host", "127.0.0.1", "127.255.255.255", ((IPEndPoint)socket.Client.LocalEndPoint!).Port, 100, true, root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
    }

    /// <summary>設定形式を変えず動作契約だけが異なる実装を表します。</summary>
    private sealed class ReviewNode(int version) : IActionNode
    {
        /// <summary>現在の動作契約と、確認する設定を宣言します。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.review", "互換性確認", "Test", "{}", []) with { ContractVersion = version, ContractNotes = "thresholdの解釈を確認してください。" };

        /// <summary>検査中に実行していないことを確認する回数。</summary>
        internal int Executions
        {
            get; private set;
        }

        /// <summary>この試験の設定形式自体は変更しません。</summary>
        /// <returns>設定の検証結果。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

        /// <summary>実行した場合だけ回数を増やします。</summary>
        /// <returns>試験用の正常結果。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions++;
            return ValueTask.FromResult(NodeResult.Success());
        }
    }
}
