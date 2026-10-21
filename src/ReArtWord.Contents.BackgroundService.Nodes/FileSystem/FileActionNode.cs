using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>許可範囲、競合管理、内部復元と全対象補償を接続します。</summary>
internal sealed class FileActionNode : ICompensableFileNode, IFileFootprintNode
{
    private readonly string operation;
    private readonly FileAccessPolicy policy;
    private readonly FileOperationCoordinator coordinator;
    private readonly FileSnapshotStore snapshots;
    private readonly FileSystemOptions options;

    /// <summary>同じPeer内のファイル能力を共有します。</summary>
    internal FileActionNode(string operation, FileAccessPolicy policy, FileOperationCoordinator coordinator, FileSnapshotStore snapshots, FileSystemOptions options)
    {
        this.operation = operation;
        this.policy = policy;
        this.coordinator = coordinator;
        this.snapshots = snapshots;
        this.options = options;
        List<PortDescriptor> inputs = [];
        bool supported1 = operation is "file.create" or "file.replace" or "file.upsert" or "file.append";
        if (supported1)
        {
            inputs.Add(new("value", EPortValueKind.Any, true));
        }
        Descriptor = new(operation, operation, "File system", "明示Peerと許可root内でファイル実体を操作します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer, inputs, [new("value")], ["success"], Schema(operation)) { HasAsyncCompletion = true };
    }

    /// <summary>操作設定と明示失敗方針です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>設定構造を検証し、実行Peerの環境検証は実行時に行います。</summary>
    /// <returns>訂正すべき問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>実行Peerで許可検査と正規化を済ませた全操作範囲です。</summary>
    /// <returns>sourceとdestinationを含む読書き範囲。</returns>
    public ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        FileOperationPlan plan = FileOperationPlan.Create(operation, new NodeSettings(context.Settings), policy);
        return ValueTask.FromResult<IReadOnlyList<FilePathAccess>>(plan.Scopes.Select(scope => new FilePathAccess(context.PeerId, scope.Path, scope.Write)).ToArray());
    }

    /// <summary>明示した一件を実行し、成功した副作用を取消で巻き戻しません。</summary>
    /// <returns>操作結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        FileSnapshot? snapshot = null;
        bool internalCapture = false;
        bool preserve = false;
        bool began = false;
        bool skipUnconfirmed = false;
        try
        {
            FileOperationPlan plan = FileOperationPlan.Create(operation, new NodeSettings(context.Settings), policy);
            snapshot = snapshots.Find(context.ExecutionId);
            if (snapshot?.PreparedResult is { } prepared)
            {
                return prepared;
            }
            if (InspectOutput(context, plan) is { } output)
            {
                if (snapshot is not null && output.Outcome == EOutcome.Succeeded)
                {
                    skipUnconfirmed = true;
                    snapshot = await snapshots.KeepUnchangedAsync(snapshot, output, cancellationToken).ConfigureAwait(false);
                    skipUnconfirmed = false;
                }
                return output;
            }
            EFileConflictPolicy conflictPolicy = context.Operational?.Settings.FileConflictPolicy ?? options.ConflictPolicy;
            EFileInternalFailurePolicy failurePolicy = context.Operational?.Settings.FileInternalFailurePolicy ?? options.InternalFailurePolicy;
            if (snapshot is null && failurePolicy == EFileInternalFailurePolicy.Restore && plan.Scopes.Any(scope => scope.Write))
            {
                snapshot = await snapshots.CaptureAsync(context.ExecutionId, plan, conflictPolicy, () => InspectOutput(context, plan), cancellationToken).ConfigureAwait(false);
                internalCapture = true;
            }
            using IDisposable lease = await AcquireAsync(plan, snapshot, conflictPolicy, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot?.PreparedResult is { } skipped)
            {
                return skipped;
            }
            if (InspectOutput(context, plan) is { } current)
            {
                if (snapshot is not null && current.Outcome == EOutcome.Succeeded)
                {
                    skipUnconfirmed = true;
                    snapshot = await snapshots.KeepUnchangedAsync(snapshot, current, cancellationToken).ConfigureAwait(false);
                    skipUnconfirmed = false;
                }
                return current;
            }
            began = true;
            NodeValue value = await FileOperations.ExecuteAsync(plan, context, policy, cancellationToken).ConfigureAwait(false);
            string skippedKey = "skipped";
            if (snapshot is not null && value.Json.ValueKind == JsonValueKind.Object && value.Json.TryGetProperty(skippedKey, out JsonElement skippedValue)
                && skippedValue.ValueKind == JsonValueKind.True)
            {
                skipUnconfirmed = true;
                snapshot = await snapshots.KeepUnchangedAsync(snapshot, NodeResult.Success(value), cancellationToken).ConfigureAwait(false);
                skipUnconfirmed = false;
            }
            return NodeResult.Success(value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or NotSupportedException)
        {
            if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "file.operation.failed", exception, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            if (skipUnconfirmed)
            {
                preserve = true;
                return NodeResult.Failure("file.skip.unconfirmed", "スキップ対象の補償除外を保存できません。既存先は変更していません。", EOutcome.Unknown);
            }
            if (internalCapture && snapshot is not null)
            {
                try
                {
                    await snapshots.RestoreAsync(snapshot.Token, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "file.restore.failed", restoreException, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    preserve = true;
                    return NodeResult.Failure("file.restore.failed", "内部復元を完了できません。退避識別子: " + snapshot.Token.Id, EOutcome.Unknown);
                }
            }
            EOutcome outcome = EOutcome.NotExecuted;
            if (began)
            {
                outcome = EOutcome.Failed;
            }
            if (!began && cancellationToken.IsCancellationRequested)
            {
                outcome = EOutcome.Cancelled;
            }
            return NodeResult.Failure("file.operation.failed", "ファイル操作を完了できません。存在前提・競合・許可範囲を確認してください。", outcome);
        }
        finally
        {
            if (internalCapture && snapshot is not null && !preserve)
            {
                await snapshots.ReleaseAsync(snapshot.Token, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>全Peer実行前にローカル対象を退避します。</summary>
    /// <returns>補償の識別子です。</returns>
    public async Task<FileCompensationToken> CaptureAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        FileOperationPlan plan = FileOperationPlan.Create(operation, new NodeSettings(context.Settings), policy);
        FileSnapshot snapshot = await snapshots.CaptureAsync(context.ExecutionId, plan, context.Operational?.Settings.FileConflictPolicy ?? options.ConflictPolicy,
            () => InspectOutput(context, plan), cancellationToken).ConfigureAwait(false);
        return snapshot.Token;
    }

    /// <summary>rootから明示された失敗補償を実施します。</summary>
    /// <returns>復元完了です。</returns>
    public Task RestoreAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        return snapshots.RestoreAsync(token, cancellationToken);
    }

    /// <summary>rootの確定後に補償状態を解放します。</summary>
    /// <returns>解放完了です。</returns>
    public Task ReleaseAsync(FileCompensationToken token, CancellationToken cancellationToken = default)
    {
        return snapshots.ReleaseAsync(token, cancellationToken);
    }

    /// <summary>読み取り・削除を変えず、保存先だけをスキップまたは占有検査します。</summary>
    /// <returns>変更を開始しない結果、または処理可能なnullです。</returns>
    private NodeResult? InspectOutput(NodeExecutionContext context, FileOperationPlan plan)
    {
        if (!WritesOutput(operation))
        {
            return null;
        }
        string target = plan.Destination ?? plan.Path;
        string createDirectory = "directory.create";
        bool reuseDirectory = FileOutputPolicy.ExistingMode(new(context.Settings)) is "overwrite";
        if (string.Equals(operation, createDirectory, StringComparison.Ordinal) && Directory.Exists(target)
            && reuseDirectory)
        {
            policy.ValidatePath(target);
            return FileOutputPolicy.Skipped(context.PeerId, target, "existing");
        }
        bool directory = operation.StartsWith("directory.", StringComparison.Ordinal);
        return FileOutputPolicy.Inspect(context, policy, target, directory);
    }

    /// <summary>保存先を持つ実体操作だけに共通の出力方針を公開します。</summary>
    /// <returns>保存方針を設定する操作かです。</returns>
    private static bool WritesOutput(string type) => type is "file.create" or "file.replace" or "file.upsert" or "file.append"
        or "file.copy" or "file.move" or "file.rename" or "directory.create" or "directory.copy" or "directory.move" or "directory.rename";

    /// <summary>captureで取得済みの場合は二重取得しません。</summary>
    /// <returns>一件実行だけが解放するリースです。</returns>
    private async Task<IDisposable> AcquireAsync(FileOperationPlan plan, FileSnapshot? snapshot, EFileConflictPolicy conflictPolicy, CancellationToken cancellationToken = default)
    {
        if (snapshot is not null)
        {
            return new FileAccessLease(() =>
            {
            });
        }
        return await coordinator.AcquireAsync(plan.Scopes, conflictPolicy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>操作ごとに有効な設定項目だけを公開します。</summary>
    /// <returns>編集と開始前検証で共用するスキーマです。</returns>
    private static JsonElement Schema(string operation)
    {
        JsonElement schema = NodeSettings.Schema("""
            {"type":"object","required":["path"],"properties":{"path":{"type":"string","minLength":1,"x-runtime-input":true},"destination":{"type":"string","minLength":1,"x-runtime-input":true},"overwrite":{"type":"boolean","default":false},"recursive":{"type":"boolean","default":false},"missingOk":{"type":"boolean","default":false},"maxBytes":{"type":"integer","minimum":1,"maximum":16777216,"default":1048576},"maxEntries":{"type":"integer","minimum":1,"maximum":100000,"default":10000},"failurePolicy":{"x-runtime-input":false,"type":"string","enum":["keep","rollbackAll"],"default":"keep"}}}
            """);
        System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
        System.Text.Json.Nodes.JsonObject properties = root["properties"]!.AsObject();
        bool supported2 = operation is not "file.copy" and not "file.move" and not "file.rename" and not "directory.copy" and not "directory.move" and not "directory.rename";
        if (supported2)
        {
            properties.Remove("destination");
            properties.Remove("overwrite");
        }
        else
        {
            root["required"]!.AsArray().Add("destination");
        }
        bool supported3 = operation is not "directory.copy" and not "directory.move" and not "directory.rename" and not "directory.delete" and not "directory.list";
        if (supported3)
        {
            properties.Remove("recursive");
        }
        bool supported4 = operation is not "file.delete" and not "directory.delete";
        if (supported4)
        {
            properties.Remove("missingOk");
        }
        string read = "file.read";
        if (!string.Equals(operation, read, StringComparison.Ordinal))
        {
            properties.Remove("maxBytes");
        }
        string list = "directory.list";
        if (!string.Equals(operation, list, StringComparison.Ordinal))
        {
            properties.Remove("maxEntries");
        }
        if (WritesOutput(operation))
        {
            FileOutputPolicy.Configure(properties, !supported2);
        }
        return JsonSerializer.SerializeToElement(root);
    }
}
