using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Clients;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>別クラスターへの転送、確認、元状態への復元と再送境界を検証します。</summary>
public sealed class ClusterArchiveServiceTests
{
    /// <summary>残すWFが使うレシピを通常WFへ置き換える取込みは、保存前に拒否します。</summary>
    /// <returns>保持する側も含めた依存関係の検証です。</returns>
    [Fact]
    public async Task MergeValidatesRetainedCallersAgainstImportedRecipes()
    {
        ArchiveTestEnvironment fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync();
        Contracts.Workflow.WorkflowDefinition recipe = ArchiveTestEnvironment.Definition("recipe", 1) with { IsRecipe = true };
        await fixture.Workflows.SaveAsync(new(Guid.NewGuid(), recipe, null));
        Contracts.Workflow.NodeDefinition call = new("call", Contracts.Workflow.SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }));
        await fixture.Workflows.SaveAsync(new(Guid.NewGuid(), new("retained", "Retained", [call], [], Enabled: true), null));
        ClusterArchiveState state = new(fixture.Cluster, fixture.Workflows, fixture.Common);
        ClusterArchiveConfiguration before = await state.CaptureAsync();
        Contracts.Workflow.WorkflowDefinition replacement = ArchiveTestEnvironment.Definition("recipe", 2);
        StoredWorkflow saved = new(replacement, ContractJson.Hash(replacement), DateTimeOffset.UtcNow, EActivationPolicy.Explicit);
        ClusterArchiveConfiguration incoming = before with { Workflows = [new(saved, replacement)] };
        using MemoryStream file = new();
        await ClusterArchiveFile.WriteAsync(file, incoming, (_, _) => Task.FromResult(new LogQueryResult([], null, true)));
        Guid id = Guid.NewGuid();
        await ArchiveFileTransfer.UploadAsync(file, id, (operation, body, token) => SendAsync(fixture.Archives, operation, body, token));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Archives.PreviewAsync(new(id, EArchiveImportMode.Merge)));
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash((await state.CaptureAsync()) with { CapturedAt = before.CapturedAt }));
        Assert.Single(fixture.Effective["retained"].RecipeInstances);
    }

    /// <summary>JSONとして読めても有効な共通設定を持たない入力は、復元先を変更しません。</summary>
    /// <returns>意味が不正なアーカイブの拒否です。</returns>
    [Fact]
    public async Task MissingOperationalStateIsRejectedBeforeImport()
    {
        ArchiveTestEnvironment fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync();
        string revision = fixture.Common.Capture()!.Revision;
        ClusterArchiveConfiguration invalid = new(1, DateTimeOffset.UtcNow, "source", "one", new(null, null), [], false);
        using MemoryStream file = new();
        await ClusterArchiveFile.WriteAsync(file, invalid, (_, _) => Task.FromResult(new LogQueryResult([], null, true)));
        Guid operationId = Guid.NewGuid();
        await ArchiveFileTransfer.UploadAsync(file, operationId, (operation, body, token) => SendAsync(fixture.Archives, operation, body, token));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Archives.PreviewAsync(new(operationId, EArchiveImportMode.Replace)));
        Assert.Equal(revision, fixture.Common.Capture()!.Revision);
        Assert.Null(await fixture.Archives.ReadAsync(operationId));
    }

    /// <summary>取込み元にだけあるレシピを事前検証し、呼出し元が先に並んでも復帰を確認します。</summary>
    /// <returns>別クラスターへの依存関係を含む復元です。</returns>
    [Fact]
    public async Task ARecipeAndItsCallerCanBeImportedIntoAnEmptyCluster()
    {
        ArchiveTestEnvironment source = new(), destination = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = destination.ConfigureAwait(true);
        await source.StartAsync();
        await destination.StartAsync();
        Contracts.Workflow.WorkflowDefinition recipe = ArchiveTestEnvironment.Definition("z-recipe", 7) with { IsRecipe = true };
        Assert.Equal(EOutcome.Succeeded, (await source.Workflows.SaveAsync(new(Guid.NewGuid(), recipe, null))).Activation.Outcome);
        Contracts.Workflow.NodeDefinition call = new("call", Contracts.Workflow.SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }));
        Contracts.Workflow.WorkflowDefinition parent = new("a-parent", "Parent", [call], [], Enabled: true);
        Assert.Equal(EOutcome.Succeeded, (await source.Workflows.SaveAsync(new(Guid.NewGuid(), parent, null))).Activation.Outcome);
        Guid export = Guid.NewGuid();
        await source.Archives.ExportAsync(new(export));
        ArchiveOperationResult exported = await WaitAsync(source.Archives, export);
        using MemoryStream file = new();
        await ArchiveFileTransfer.DownloadAsync(file, exported.File!, (operation, body, token) => SendAsync(source.Archives, operation, body, token));
        Guid import = Guid.NewGuid();
        await ArchiveFileTransfer.UploadAsync(file, import, (operation, body, token) => SendAsync(destination.Archives, operation, body, token));
        ArchiveImportPreview preview = await destination.Archives.PreviewAsync(new(import, EArchiveImportMode.Replace));
        await destination.Archives.ImportAsync(new(import, preview.Confirmation));
        ArchiveOperationResult result = await WaitAsync(destination.Archives, import);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result, ContractJson.Options) + Environment.NewLine + string.Join(Environment.NewLine, destination.Errors));
        Assert.True(destination.Effective[parent.Id].Definition.Enabled);
        Assert.Single(destination.Effective[parent.Id].RecipeInstances);
        Assert.Empty(destination.Errors);
    }

    /// <summary>バイト転送から完了照会まで通し、同じ要求で再度変更しません。</summary>
    /// <returns>共通設定・有効状態・ログの実保存確認です。</returns>
    [Theory]
    [InlineData(EArchiveImportMode.Merge)]
    [InlineData(EArchiveImportMode.Replace)]
    public async Task TransfersAndRestoresAnArchiveWithAnExplicitPreview(EArchiveImportMode mode)
    {
        ArchiveTestEnvironment source = new(), destination = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = destination.ConfigureAwait(true);
        await source.StartAsync();
        await destination.StartAsync();
        WorkflowSaveResult active = await source.Workflows.SaveAsync(new(Guid.NewGuid(), ArchiveTestEnvironment.Definition("screen", 1), null));
        WorkflowSaveResult draft = await source.Workflows.SaveAsync(new(Guid.NewGuid(), ArchiveTestEnvironment.Definition("screen", 2) with { Enabled = false }, active.Revision, EActivationPolicy.Explicit));
        WorkflowLogEntry log = new(Guid.NewGuid(), DateTimeOffset.UtcNow, "INFO", "one", "Archived event");
        await source.Log.WriteAsync(log);
        Guid export = Guid.NewGuid();
        await source.Archives.ExportAsync(new(export, true));
        ArchiveOperationResult exported = await WaitAsync(source.Archives, export);
        Assert.Equal(EOutcome.Succeeded, exported.Outcome);
        using MemoryStream archive = new();
        await ArchiveFileTransfer.DownloadAsync(archive, exported.File!, (operation, body, token) => SendAsync(source.Archives, operation, body, token));
        Guid import = Guid.NewGuid();
        await ArchiveFileTransfer.UploadAsync(archive, import, (operation, body, token) => SendAsync(destination.Archives, operation, body, token));
        await destination.Workflows.SaveAsync(new(Guid.NewGuid(), ArchiveTestEnvironment.Definition("extra", 3), null));
        ArchiveImportPreview preview = await destination.Archives.PreviewAsync(new(import, mode));
        Assert.Equal(source.Cluster.LocalClusterId, preview.SourceCluster);
        Assert.Equal(1, preview.LogEntries);
        Assert.Equal("screen", Assert.Single(preview.Added));
        Assert.Equal(mode == EArchiveImportMode.Replace, preview.Deleted.Count == 1);
        ArchiveImportRequest request = new(import, preview.Confirmation);
        await destination.Archives.ImportAsync(request);
        ArchiveOperationResult[] observations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => WaitAsync(destination.Archives, import)));
        Assert.All(observations, observation => Assert.True(observation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(observation, ContractJson.Options)));
        ArchiveOperationResult result = observations[0];
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result, ContractJson.Options) + Environment.NewLine + string.Join(Environment.NewLine, destination.Errors));
        Assert.Equal(draft.Revision, (await destination.Workflows.GetAsync("screen"))!.Revision);
        Assert.Equal(active.Revision, destination.Effective["screen"].Revision);
        Assert.Equal(mode == EArchiveImportMode.Merge, (await destination.Workflows.GetAsync("extra")) is not null);
        Assert.Equal(log.Id, Assert.Single((await destination.Log.QueryAsync(new())).Entries).Id);
        string fingerprint = ContractJson.Hash(result);
        Assert.Equal(fingerprint, ContractJson.Hash(await destination.Archives.ImportAsync(request)));
        Assert.Empty(source.Errors);
        Assert.Empty(destination.Errors);
    }

    /// <summary>確認後に復元先が変わった場合は、取り込み元で上書きしません。</summary>
    /// <returns>確認値の変化と未実行結果です。</returns>
    [Fact]
    public async Task AChangedDestinationInvalidatesThePreviewBeforeConfigurationWrites()
    {
        ArchiveTestEnvironment fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync();
        Guid export = Guid.NewGuid();
        await fixture.Archives.ExportAsync(new(export));
        ArchiveOperationResult exported = await WaitAsync(fixture.Archives, export);
        using MemoryStream file = new();
        await ArchiveFileTransfer.DownloadAsync(file, exported.File!, (operation, body, token) => SendAsync(fixture.Archives, operation, body, token));
        Guid import = Guid.NewGuid();
        await ArchiveFileTransfer.UploadAsync(file, import, (operation, body, token) => SendAsync(fixture.Archives, operation, body, token));
        ArchiveImportPreview preview = await fixture.Archives.PreviewAsync(new(import, EArchiveImportMode.Replace));
        await fixture.Workflows.SaveAsync(new(Guid.NewGuid(), ArchiveTestEnvironment.Definition("new-edit", 9), null));
        await fixture.Archives.ImportAsync(new(import, preview.Confirmation));
        ArchiveOperationResult result = await WaitAsync(fixture.Archives, import);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("Archive.PreviewChanged", result.Reason);
        Assert.NotNull(await fixture.Workflows.GetAsync("new-edit"));
        Assert.Empty(fixture.Errors);
    }

    /// <summary>共通クライアントの実転送境界を使います。</summary>
    /// <returns>JSONで直列化した実応答です。</returns>
    private static async Task<JsonElement> SendAsync(ClusterArchiveService service, string operation, object body, CancellationToken cancellationToken = default)
    {
        object response = operation switch
        {
            "archive.read" => await service.ReadChunkAsync((ArchiveReadRequest)body, cancellationToken).ConfigureAwait(false),
            "archive.upload.begin" => await service.BeginUploadAsync((ArchiveTransfer)body, cancellationToken).ConfigureAwait(false),
            "archive.upload.chunk" => await service.UploadAsync((ArchiveChunk)body, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(),
        };
        return JsonSerializer.SerializeToElement(response, ContractJson.Options);
    }

    /// <summary>一回の操作結果だけを有限時間照会します。</summary>
    /// <returns>確定した操作状態です。</returns>
    private static async Task<ArchiveOperationResult> WaitAsync(ClusterArchiveService service, Guid id, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            ArchiveOperationResult? result = await service.ReadAsync(id, timeout.Token).ConfigureAwait(false);
            if (result is { Completed: true })
            {
                return result;
            }
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("Archive operation did not finish.");
    }
}
