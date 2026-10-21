using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>明示source Peerの内容を分割取得し、実行Peerの宛先へ確定します。</summary>
internal sealed class FileTransferNode : ICompensableFileNode, IFileFootprintNode
{
    private readonly FileTransferService service;
    private readonly IPeerFileTransfer? peers;
    private readonly FileActionNode compensation;

    /// <summary>宛先側の共通ファイルサービスとPeer ポートを使用します。</summary>
    internal FileTransferNode(FileTransferService service, IPeerFileTransfer? peers)
    {
        this.service = service;
        this.peers = peers;
        compensation = new("file.upsert", service.AccessPolicy, service.Operations, service.Snapshots, service.Options);
    }

    /// <summary>source、宛先、明示再要求と全対象失敗方針です。</summary>
    public NodeDescriptor Descriptor => new("file.transfer", "Host file transfer", "File system", "有限chunkとSHA256でfile/treeを転送します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [], [new("value"), new("requestId", EPortValueKind.Json)], ["success"], OutputSchema("""
        {"type":"object","required":["sourcePath","path"],"properties":{"sourceHost":{"type":"string","minLength":1},"sourcePeer":{"type":"string","minLength":1},"sourcePath":{"type":"string","minLength":1,"x-runtime-input":true},"path":{"type":"string","minLength":1,"x-runtime-input":true},"requestId":{"type":"string","minLength":1,"description":"Leave empty for a unique transfer ID for each execution. Set an ID only to explicitly resume the same source and content."},"overwrite":{"type":"boolean","default":false},"recursive":{"type":"boolean","default":false,"description":"Enable to transfer a directory and all its contents, including empty folders."},"maxEntries":{"type":"integer","minimum":1,"maximum":100000,"default":10000},"failurePolicy":{"x-runtime-input":false,"type":"string","enum":["keep","rollbackAll"],"default":"keep"}}}
        """)) { HasAsyncCompletion = true };

    /// <summary>他Peerの環境を解決せず転送設定の構造を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. NodeSchemaValidator.Validate(definition, Descriptor)];
        string hostKey = "sourceHost";
        string peerKey = "sourcePeer";
        if (definition.Settings.ValueKind == JsonValueKind.Object && definition.Settings.TryGetProperty(hostKey, out _) == definition.Settings.TryGetProperty(peerKey, out _))
        {
            issues.Add(new ValidationIssue("file.source.required", "転送元Hostを指定してください。旧形式のsourcePeerとは併用できません。", definition.Id));
        }
        return issues;
    }

    /// <summary>宛先の変更と、source Peerで検証する読取り範囲を宣言します。</summary>
    /// <returns>両実体で予約する範囲。</returns>
    public ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeSettings settings = new(context.Settings);
        string sourcePeer = settings.ReadText("sourcePeer") ?? string.Empty;
        string sourcePath = settings.ReadText("sourcePath") ?? string.Empty;
        string destination = service.AccessPolicy.ResolvePath(settings.ReadText("path") ?? string.Empty);
        if (string.Equals(sourcePeer, context.PeerId, StringComparison.Ordinal))
        {
            sourcePath = service.AccessPolicy.ResolvePath(sourcePath, true);
        }
        return ValueTask.FromResult<IReadOnlyList<FilePathAccess>>([new(sourcePeer, sourcePath, false), new(context.PeerId, destination, true)]);
    }

    /// <summary>再送を暗黙実行せず、一件の転送と最終検証を行います。</summary>
    /// <returns>宛先確定結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        FileCompensationToken? internalSnapshot = null;
        bool committed = false;
        bool preserve = false;
        bool skipUnconfirmed = false;
        string requestId = context.ExecutionId.ToString("N");
        try
        {
            NodeSettings settings = new(context.Settings);
            requestId = settings.ReadText("requestId") ?? requestId;
            string sourcePeer = settings.ReadText("sourcePeer") ?? string.Empty;
            string sourcePath = settings.ReadText("sourcePath") ?? string.Empty;
            string destination = service.AccessPolicy.ResolvePath(settings.ReadText("path") ?? string.Empty);
            NodeResult? prepared = service.Snapshots.Find(context.ExecutionId)?.PreparedResult;
            if (prepared is not null)
            {
                return WithRequestId(prepared, requestId);
            }
            if (FileOutputPolicy.Inspect(context, service.AccessPolicy, destination, directory: null) is { } output)
            {
                if (service.Snapshots.Find(context.ExecutionId) is { } snapshot && output.Outcome == EOutcome.Succeeded)
                {
                    skipUnconfirmed = true;
                    await service.Snapshots.KeepUnchangedAsync(snapshot, output, cancellationToken).ConfigureAwait(false);
                    skipUnconfirmed = false;
                }
                return WithRequestId(output, requestId);
            }
            if (string.Equals(sourcePeer, context.PeerId, StringComparison.Ordinal))
            {
                _ = FileOperationPlan.Create("file.copy", new NodeSettings(JsonSerializer.SerializeToElement(new { path = sourcePath, destination })), service.AccessPolicy);
            }
            FileTransferSource source = new(sourcePeer, context.PeerId, service, peers, context.Operational);
            int maximum = settings.ReadInt32("maxEntries", 10000);
            IReadOnlyList<FileTransferItem> items = await FileTransferPlan.CollectAsync(source, sourcePath, maximum, cancellationToken).ConfigureAwait(false);
            bool directory = items[0].Entry.IsDirectory;
            bool recursive = settings.ReadBoolean("recursive");
            if (directory && !recursive)
            {
                return WithRequestId(NodeResult.Failure("file.transfer.recursive.required", "directory転送にはrecursiveを明示してください。", EOutcome.NotExecuted), requestId);
            }
            EFileInternalFailurePolicy failurePolicy = context.Operational?.Settings.FileInternalFailurePolicy ?? service.Options.InternalFailurePolicy;
            if (failurePolicy == EFileInternalFailurePolicy.Restore && service.Snapshots.Find(context.ExecutionId) is null)
            {
                internalSnapshot = await CaptureAsync(context, cancellationToken).ConfigureAwait(false);
                if (service.Snapshots.Find(context.ExecutionId)?.PreparedResult is { } captured)
                {
                    return WithRequestId(captured, requestId);
                }
            }
            FileOutputPolicy.EnsureParent(service.AccessPolicy, destination);
            string hash = FileTransferManifest.ComputeSha256(items.Select(item => item.Entry).ToArray());
            string mode = FileOutputPolicy.ExistingMode(settings);
            FileStageRequest request = new(requestId, sourcePeer, sourcePath, destination, hash, directory, mode is "overwrite", context.ExecutionId,
                context.Operational, mode is "skip", settings.ReadText("lockedFileMode", "error") is "skip");
            FileStageToken stage = await service.OpenStageAsync(request, cancellationToken).ConfigureAwait(false);
            await FileTransferPlan.CopyAsync(source, service, stage, items, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<FileTransferItem> verified = await FileTransferPlan.CollectAsync(source, sourcePath, maximum, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, FileTransferManifest.ComputeSha256(verified.Select(item => item.Entry).ToArray()), StringComparison.Ordinal))
            {
                throw new IOException("転送中にsource内容またはtree構成が変更されました。");
            }
            committed = true;
            FileStageCommitResult result = await service.CommitStageAsync(stage, cancellationToken).ConfigureAwait(false);
            if (result.Skipped)
            {
                NodeResult skipped = FileOutputPolicy.Skipped(context.PeerId, destination, result.SkipReason!);
                if (service.Snapshots.Find(context.ExecutionId) is { } snapshot)
                {
                    skipUnconfirmed = true;
                    await service.Snapshots.KeepUnchangedAsync(snapshot, skipped, cancellationToken).ConfigureAwait(false);
                    skipUnconfirmed = false;
                }
                await service.DiscardStageAsync(stage, CancellationToken.None).ConfigureAwait(false);
                return WithRequestId(skipped, requestId);
            }
            return WithRequestId(NodeResult.Success(NodeValue.FromObject(result)), requestId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "file.transfer.failed", exception, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            if (skipUnconfirmed)
            {
                preserve = true;
                return WithRequestId(NodeResult.Failure("file.skip.unconfirmed", "スキップ対象の補償除外を保存できません。既存先は変更していません。", EOutcome.Unknown), requestId);
            }
            if (exception is FileStagePolicyMismatchException)
            {
                return WithRequestId(NodeResult.Failure("file.stage.policyMismatch", "保持stageと共通ファイル方針が異なります。新requestIdで新規転送を指定してください。", EOutcome.NotExecuted), requestId);
            }
            if (internalSnapshot is not null)
            {
                try
                {
                    await RestoreAsync(internalSnapshot, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "file.transfer.restore.failed", restoreException, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    preserve = true;
                    return WithRequestId(NodeResult.Failure("file.transfer.restore.failed", "転送の内部復元を完了できません。", EOutcome.Unknown), requestId);
                }
            }
            EOutcome outcome = EOutcome.Failed;
            if (committed)
            {
                outcome = EOutcome.Unknown;
            }
            return WithRequestId(NodeResult.Failure("file.transfer.failed", "転送を完了できません。同じrequestId/source/contentで明示再要求すると確認済み部分を再利用できます。", outcome), requestId);
        }
        finally
        {
            if (internalSnapshot is not null && !preserve)
            {
                await ReleaseAsync(internalSnapshot, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>自動発行したIDも、成功・失敗の結果から明示再開用に確認できます。</summary>
    /// <returns>転送IDを含む結果です。</returns>
    private static NodeResult WithRequestId(NodeResult result, string requestId) => result with
    {
        Outputs = new Dictionary<string, NodeValue>(result.Outputs, StringComparer.Ordinal) { ["requestId"] = NodeValue.FromObject(requestId) },
    };

    /// <summary>宛先を全対象実行前に退避します。</summary>
    /// <returns>退避識別子です。</returns>
    public async Task<FileCompensationToken> CaptureAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        FileOperationPlan plan = FileOperationPlan.Create("file.upsert", new(context.Settings), service.AccessPolicy);
        Snapshots.FileSnapshot snapshot = await service.Snapshots.CaptureAsync(context.ExecutionId, plan,
            context.Operational?.Settings.FileConflictPolicy ?? service.Options.ConflictPolicy,
            () => FileOutputPolicy.Inspect(context, service.AccessPolicy, plan.Path, directory: null), cancellationToken).ConfigureAwait(false);
        return snapshot.Token;
    }

    /// <summary>明示された全対象補償を実施します。</summary>
    /// <returns>復元完了です。</returns>
    public Task RestoreAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        return compensation.RestoreAsync(token, cancellationToken);
    }

    /// <summary>宛先退避を解放します。</summary>
    /// <returns>解放完了です。</returns>
    public Task ReleaseAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        return compensation.ReleaseAsync(token, cancellationToken);
    }

    /// <summary>既存の転送条件を保ち、共通の保存方針を追加します。</summary>
    /// <returns>新規配置と保存済み設定の双方を扱うスキーマです。</returns>
    private static JsonElement OutputSchema(string source)
    {
        System.Text.Json.Nodes.JsonObject schema = System.Text.Json.Nodes.JsonNode.Parse(source)!.AsObject();
        FileOutputPolicy.Configure(schema["properties"]!.AsObject(), legacy: true);
        return JsonSerializer.SerializeToElement(schema);
    }
}
