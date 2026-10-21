using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterConflicts;

/// <summary>分岐表示の秘密境界と、影響するノードだけを保留する判定を検証します。</summary>
public sealed class ConflictInspectionTests
{
    /// <summary>予定位置の分岐は該当タイマーだけに限定し、選び直しによる巻戻しを拒否します。</summary>
    [Fact]
    public void ScheduleConflictDoesNotBlockUnrelatedSourcesOrAllowRewind()
    {
        ScheduleProgressRecord progress = new("n", new("n:key", null, DateTimeOffset.UnixEpoch, Count: 1), Guid.NewGuid(), []);
        string scope = ScheduleCursorStore.ScopePrefix + "w";
        DurableEntry first = Entry(scope, "n:key", "one", NodeValue.FromObject(progress));
        DurableEntry second = Entry(scope, "n:key", "two", NodeValue.FromObject(progress with { Cursor = progress.Cursor with { Count = 2 } }));
        StateConflict conflict = ConflictInspection.Describe([first, second]);
        NodeRegistry registry = new([new Nodes.Scheduling.ScheduleNode(), new InspectionNode("pass", "Control")]);
        Assert.False(conflict.Resolvable);
        Assert.Equal(["w"], conflict.WorkflowIds);
        Assert.NotNull(ConflictInspection.GetExecutionProblem([conflict], "w", Node("scheduling.timer"), registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([conflict], "w", Node("scheduling.timer") with { Id = "other" }, registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([conflict], "other", Node("scheduling.timer"), registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([conflict], "w", Node("pass"), registry));
        StateConflict equal = ConflictInspection.Describe([first, first with { Version = "two" }]);
        Assert.True(equal.Resolvable);
        Assert.Empty(ConflictCandidateValidation.Validate(equal, first.Value, new Runtime.Workflows.Compilation.WorkflowCompiler(registry)));
        Assert.NotEmpty(ConflictCandidateValidation.Validate(equal, second.Value, new Runtime.Workflows.Compilation.WorkflowCompiler(registry)));
        Assert.NotEmpty(ConflictCandidateValidation.Validate(equal, null, new Runtime.Workflows.Compilation.WorkflowCompiler(registry)));
    }

    /// <summary>timezone差分は日時ノードだけへ影響し、ファイル方針はファイル操作に対応するノードだけへ影響します。</summary>
    [Fact]
    public void OperationalDifferencesOnlyBlockActualDependencies()
    {
        ClusterOperationalSettings settings = Settings();
        StateConflict timezone = Operational(settings, settings with { TimeZoneId = "Tokyo Standard Time" });
        NodeRegistry registry = Registry();
        Assert.Equal(["TimeZoneId"], timezone.OperationalFields);
        Assert.True(timezone.Resolvable);
        Assert.NotNull(ConflictInspection.GetExecutionProblem([timezone], "w", Node("control.condition", new { operation = "timeWindow" }), registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([timezone], "w", Node("control.condition", new { operation = "equal" }), registry));
        Assert.NotNull(ConflictInspection.GetExecutionProblem([timezone], "w", Node("schedule"), registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([timezone], "w", Node("pass"), registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([timezone], "w", Node("file.read"), registry));
        StateConflict file = Operational(settings, settings with { FileConflictPolicy = EFileConflictPolicy.Wait });
        Assert.NotNull(ConflictInspection.GetExecutionProblem([file], "w", Node("file.read"), registry));
        Assert.Null(ConflictInspection.GetExecutionProblem([file], "w", Node("schedule"), registry));
    }

    /// <summary>HostGroup差分は名前を指定したノードだけに影響します。</summary>
    [Fact]
    public void GroupDifferenceDoesNotBlockUnrelatedTargets()
    {
        ClusterOperationalSettings before = Settings() with { HostGroups = new Dictionary<string, IReadOnlyList<string>> { ["room-a"] = ["host-a"], ["room-b"] = ["host-b"] } };
        ClusterOperationalSettings after = before with { HostGroups = new Dictionary<string, IReadOnlyList<string>> { ["room-a"] = ["host-c"], ["room-b"] = ["host-b"] } };
        StateConflict conflict = Operational(before, after);
        Assert.Equal(["HostGroups/room-a"], conflict.OperationalFields);
        Assert.NotNull(ConflictInspection.GetExecutionProblem([conflict], "w", Node("pass") with { Target = new(EExecutionScope.All, Groups: ["room-a"]) }, Registry()));
        Assert.Null(ConflictInspection.GetExecutionProblem([conflict], "w", Node("pass") with { Target = new(EExecutionScope.All, Groups: ["room-b"]) }, Registry()));
    }

    /// <summary>秘密分岐の生値はDTOへ入れず、参照する名前だけを隔離します。</summary>
    [Fact]
    public void SecretCandidatesRemainHiddenAndReferenceScoped()
    {
        StateConflict conflict = ConflictInspection.Describe([Entry("secrets", "api", "one", NodeValue.FromObject("secret-one")), Entry("secrets", "api", "two", NodeValue.FromObject("secret-two"))]);
        Assert.True(conflict.ValuesHidden);
        Assert.True(conflict.Resolvable);
        Assert.All(conflict.Candidates, static candidate => Assert.Null(candidate.Value));
        Assert.DoesNotContain("secret-one", JsonSerializer.Serialize(conflict), StringComparison.Ordinal);
        Assert.NotNull(ConflictInspection.GetExecutionProblem([conflict], "w", Node("http", new { secretRef = "api" }), Registry()));
        Assert.Null(ConflictInspection.GetExecutionProblem([conflict], "w", Node("http", new { secretRef = "other" }), Registry()));
    }

    /// <summary>未知用途と型が壊れた既知候補は全体保留とし、同値保護条件だけは解消可能です。</summary>
    [Fact]
    public void UnsupportedOrMalformedCandidateCannotBeTreatedAsArbitraryValue()
    {
        StateConflict unknown = ConflictInspection.Describe([Entry("unknown", "key", "one", NodeValue.FromObject(1))]);
        StateConflict broken = ConflictInspection.Describe([Entry("workflows", "w", "one", NodeValue.FromObject(new { broken = true }))]);
        Assert.False(unknown.Resolvable);
        Assert.False(broken.Resolvable);
        Assert.NotNull(ConflictInspection.GetExecutionProblem([unknown], "w", Node("pass"), Registry()));
        Assert.NotNull(ConflictInspection.GetExecutionProblem([broken], "other", Node("pass"), Registry()));
        StateConflict equal = ConflictInspection.Describe([Entry("snapshot.edit-locks", "w", "one", NodeValue.FromObject(Array.Empty<int>())), Entry("snapshot.edit-locks", "w", "two", NodeValue.FromObject(Array.Empty<int>()))]);
        Assert.True(equal.Resolvable);
        Assert.Null(ConflictInspection.GetExecutionProblem([equal], "w", Node("pass"), Registry()));
    }

    /// <summary>定義分岐は対象ワークフローを隔離し、状態値分岐は状態利用ノードだけへ限定します。</summary>
    [Fact]
    public void WorkflowAndRuntimeStateHaveDifferentDependencyScopes()
    {
        WorkflowDefinition definition = new("w", "workflow", [Node("pass")], []);
        StoredWorkflow saved = new(definition, ContractJson.Hash(definition), DateTimeOffset.UnixEpoch, EActivationPolicy.Auto);
        StateConflict conflict = ConflictInspection.Describe([Entry("workflow.history:w", saved.Revision, "one", NodeValue.FromObject(saved))]);
        Assert.True(conflict.Resolvable);
        Assert.Equal(["w"], conflict.WorkflowIds);
        Assert.NotNull(ConflictInspection.GetExecutionProblem([conflict], "w", Node("pass"), Registry()));
        Assert.Null(ConflictInspection.GetExecutionProblem([conflict], "other", Node("pass"), Registry()));
        StateConflict state = ConflictInspection.Describe([Entry("workflow:w", "key", "one", NodeValue.FromObject(1)), Entry("workflow:w", "key", "two", NodeValue.FromObject(2))]);
        Assert.Null(ConflictInspection.GetExecutionProblem([state], "w", Node("pass"), Registry()));
        Assert.NotNull(ConflictInspection.GetExecutionProblem([state], "w", Node("state"), Registry()));
    }

    /// <summary>実保存形式で共通設定の二候補を生成します。</summary>
    /// <returns>比較結果です。</returns>
    private static StateConflict Operational(ClusterOperationalSettings before, ClusterOperationalSettings after)
    {
        ClusterConfigurationStored first = new(new(before, ContractJson.Hash(before), DateTimeOffset.UnixEpoch), EActivationPolicy.Auto);
        ClusterConfigurationStored second = new(new(after, ContractJson.Hash(after), DateTimeOffset.UnixEpoch), EActivationPolicy.Auto);
        return ConflictInspection.Describe([Entry("cluster.operational", "cluster-operational", "one", NodeValue.FromObject(first)), Entry("cluster.operational", "cluster-operational", "two", NodeValue.FromObject(second))]);
    }

    /// <summary>共通既定の有効な設定です。</summary>
    /// <returns>独立した設定です。</returns>
    private static ClusterOperationalSettings Settings() => new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>内容以外の保存情報を固定します。</summary>
    /// <returns>保持候補です。</returns>
    private static DurableEntry Entry(string scope, string key, string version, NodeValue value) => new(scope, key, version, value);

    /// <summary>登録名と明示設定を持つノードです。</summary>
    /// <returns>設定定義です。</returns>
    private static NodeDefinition Node(string type, object? settings = null) => new("n", type, JsonSerializer.SerializeToElement(settings ?? new { }));

    /// <summary>ファイル操作に対応するノードと副作用のない試験ノードを登録します。</summary>
    /// <returns>依存を判別する登録です。</returns>
    private static NodeRegistry Registry() => new([new InspectionNode("pass", "Control"), new InspectionNode("control.condition", "Control"), new InspectionNode("schedule", "Scheduling"), new InspectionNode("http", "Communication"), new InspectionNode("state", "State"), .. FileSystemNodeRegistration.Create(new([Path.GetTempPath()]))]);

    /// <summary>実行されないメタデータ用ノードです。</summary>
    private sealed class InspectionNode(string type, string category) : IActionNode
    {
        /// <summary>試験する登録情報です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe(type, type, category, "{}", []);

        /// <summary>試験用設定に追加制約はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>依存判定では呼び出してはいけません。</summary>
        /// <returns>実行不可の結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("依存判定で副作用を実行してはいけません。");
    }
}
