using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>Readyや共有保存を前提とせず、同期済み反映希望だけをローカル準備します。</summary>
public sealed class LocalPreparationTests
{
    /// <summary>初回空・確認済み共有版・不正反映希望を区別し、準備を共有履歴へ追記しません。</summary>
    /// <returns>Ready前の共通運用設定採用の確認です。</returns>
    [Fact]
    public async Task CommonPreparationAdoptsConfirmedVersionWithoutReadyOrSharedWrites()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        IDurableRepository repository = fixture.Repositories["a"];
        using ClusterConfigurationStore seed = new(repository, fixture.Peers["a"]);
        Assert.Equal(ELocalPreparationState.Empty, (await seed.PrepareLocalAsync()).State);
        ClusterOperationalSettings settings = new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        Assert.Equal(EOutcome.Succeeded, (await seed.InitializeAsync(settings, true)).Outcome);
        await fixture.StopAsync("a");
        using ClusterConfigurationStore restored = new(repository, fixture.Peers["a"]);
        RepositorySnapshot before = await repository.ExportAsync();
        Assert.Equal(ELocalPreparationState.Prepared, (await restored.PrepareLocalAsync()).State);
        Assert.Equal(seed.Capture()!.Revision, restored.Capture()!.Revision);
        Assert.Equal(ELocalPreparationState.Prepared, (await restored.PrepareLocalAsync()).State);
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await repository.ExportAsync()));
        DurableEntry marker = (await repository.ReadAsync("cluster.operational.activation", ClusterConfigurationStore.Id))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation(marker.Scope, marker.Key, marker.Version, NodeValue.FromObject(new { Settings = (object?)null }))]);
        Assert.Equal(ELocalPreparationState.Unavailable, (await restored.PrepareLocalAsync()).State);
    }

    /// <summary>不正な一定義を隔離しても別定義を準備し、次回確認でローカル操作記録を増やしません。</summary>
    /// <returns>ワークフロー単位の準備と隔離の確認です。</returns>
    [Fact]
    public async Task WorkflowPreparationIsolatesInvalidDefinitionAndPreservesIndependentPlan()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StopAsync("a");
        IDurableRepository repository = fixture.Repositories["a"];
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = new(repository, fixture.Peers["a"], new WorkflowCompiler(new NodeRegistry([])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (plan, token) =>
            {
                effective[plan.Definition.Id] = plan;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
            DeactivateHandler = (id, token) =>
            {
                effective.Remove(id);
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        Assert.Equal(ELocalPreparationState.Empty, (await store.PrepareLocalAsync()).State);
        WorkflowDefinition good = new("good", "good", [], []);
        WorkflowDefinition bad = good with { Id = "bad" };
        StoredWorkflow one = new(good, ContractJson.Hash(good), DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        StoredWorkflow two = new(bad, ContractJson.Hash(bad), DateTimeOffset.UtcNow, EActivationPolicy.Auto);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow.activation", "good", null, NodeValue.FromObject(one)), new("workflow.activation", "bad", null, NodeValue.FromObject(two))]);
        Assert.Empty((await store.PrepareLocalAsync()).Issues);
        Assert.Equal(2, effective.Count);
        DurableEntry previous = (await repository.ReadAsync("workflow.activation", "bad"))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(previous.Scope, previous.Key, previous.Version, NodeValue.FromObject(two with { Revision = "wrong" }))]);
        LocalPreparationResult preparation = await store.PrepareLocalAsync();
        Assert.Equal(ELocalPreparationState.Prepared, preparation.State);
        Assert.Single(preparation.Issues);
        Assert.Equal("good", Assert.Single(effective).Key);
        RepositorySnapshot before = await repository.ExportAsync();
        await store.PrepareLocalAsync();
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await repository.ExportAsync()));
        effective.Remove("good");
        await store.PrepareLocalAsync().ConfigureAwait(true);
        Assert.Equal(one.Revision, effective["good"].Revision);
        DurableEntry goodMarker = (await repository.ReadAsync("workflow.activation", "good").ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(goodMarker.Scope, goodMarker.Key, goodMarker.Version, null)]).ConfigureAwait(true);
        await store.PrepareLocalAsync().ConfigureAwait(true);
        Assert.Empty(effective);
    }
}
