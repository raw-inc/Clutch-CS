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

/// <summary>別クラスターへの復元と、同じ操作を再送した場合の動作を検証します。</summary>
public sealed class ClusterArchiveServiceTests
{
    /// <summary>残すWFが参照するレシピを通常WFに置き換えようとすると、復元前に拒否されることを確認します。</summary>
    /// <returns>復元先に残すWFの依存関係の検証。</returns>
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

    /// <summary>JSONの構文が正しくても、共通設定が欠けていれば復元前に拒否されることを確認します。</summary>
    /// <returns>不正な設定の検証。</returns>
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

    /// <summary>復元先に存在しないレシピも、そのレシピを使うWFと一緒に復元できることを確認します。</summary>
    /// <returns>レシピと呼び出し元WFの復元の検証。</returns>
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

    /// <summary>転送から復元まで実行し、同じ要求を再送しても復元し直さないことを確認します。</summary>
    /// <returns>設定、有効状態、ログの復元の検証。</returns>
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

    /// <summary>事前確認後に復元先の設定が変わった場合、復元を開始しないことを確認します。</summary>
    /// <returns>事前確認後の変更を検出する処理の検証。</returns>
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

    /// <summary>アーカイブサービスを呼び出し、転送クライアント用の応答を生成します。</summary>
    /// <returns>サービスの応答を表すJSON。</returns>
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

    /// <summary>指定した操作が完了するまで、期限を設けて結果を照会します。</summary>
    /// <returns>完了した操作の結果。</returns>
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
