using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.JavaScript;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>Peer単位で一つの導入を所有し、短い受付と完了確認を分けます。</summary>
public sealed class ScriptPackageManager : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly ScriptPackageStore store;
    private readonly string nugetSource;
    private readonly string? wheelDirectory;
    private readonly string npmRegistry;
    private readonly ScriptRuntimeRegistry? runtimes;
    private readonly CancellationTokenSource stopping = new();
    private Task? running;
    private bool disposed;

    /// <summary>公開配布元を既定とし、試験時はローカル配布物で同じ復元経路を検証できます。</summary>
    public ScriptPackageManager(ScriptPackageStore store, string nugetSource = "https://api.nuget.org/v3/index.json", string? wheelDirectory = null, string npmRegistry = "https://registry.npmjs.org", ScriptRuntimeRegistry? runtimes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.nugetSource = nugetSource;
        this.wheelDirectory = wheelDirectory;
        this.npmRegistry = npmRegistry;
        this.runtimes = runtimes;
        foreach (ScriptPackageOperation interrupted in store.Operations().Where(operation => !operation.Completed))
        {
            store.Save(interrupted with { Phase = "interrupted", Completed = true, Outcome = EOutcome.Unknown, UpdatedAt = DateTimeOffset.UtcNow, Reason = "script.packages.interrupted" });
        }
    }

    /// <summary>画面の更新は導入を再送せず、有限件の結果と現在構成だけを取得します。</summary>
    /// <returns>Peerローカルの現在状態です。</returns>
    public ScriptPackageCatalog Catalog()
    {
        using (gate.EnterScope())
        {
            return new(store.Current(EScriptPackageKind.Nuget), store.Current(EScriptPackageKind.Pip), store.Operations().Take(20).ToArray(), running is { IsCompleted: false }, store.Current(EScriptPackageKind.Npm));
        }
    }

    /// <summary>副作用の開始前に元操作を記録し、再送と構成競合を判定します。</summary>
    /// <returns>受付済みまたは拒否した操作です。受付はインストール成功を意味しません。</returns>
    // 抑制理由: 導入Taskはrunningへ保存し、DisposeAsyncで取消とawaitを行います。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "受付後の処理を所有し、終了時にawaitします。")]
    public ScriptPackageOperation Change(ScriptPackageChangeRequest request)
    {
        request = ScriptPackageRules.Validate(request);
        string fingerprint = ContractJson.Hash(request);
        using (gate.EnterScope())
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ScriptPackageOperation? existing = store.Operation(request.OperationId);
            if (existing is not null)
            {
                if (string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return existing;
                }
                return new(request, fingerprint, "rejected", EOutcome.NotExecuted, true, DateTimeOffset.UtcNow, Reason: "script.packages.operationMismatch");
            }
            ScriptPackageEnvironmentInfo? current = store.Current(request.Kind);
            string? reason = null;
            if (running is { IsCompleted: false })
            {
                reason = "script.packages.busy";
            }
            if (reason is null && !string.Equals(request.ExpectedRevision, current?.Revision, StringComparison.Ordinal))
            {
                reason = "script.packages.configurationChanged";
            }
            if (reason is null && runtimes is null && request.Kind == EScriptPackageKind.Pip && string.IsNullOrWhiteSpace(request.PythonPath ?? current?.PythonPath))
            {
                reason = "script.packages.pythonPathRequired";
            }
            if (reason is null && runtimes is null && request.Kind == EScriptPackageKind.Npm && string.IsNullOrWhiteSpace(request.NodePath ?? current?.NodePath))
            {
                reason = "script.packages.nodePathRequired";
            }
            List<ScriptPackageReference> packages = current?.Requested.Where(package => !string.Equals(package.Id, request.Package.Id, StringComparison.Ordinal)).ToList() ?? [];
            if (!request.Remove)
            {
                packages.Add(request.Package);
            }
            if (32 < packages.Count)
            {
                reason = "script.packages.packageLimit";
            }
            if (reason is not null)
            {
                ScriptPackageOperation rejected = new(request, fingerprint, "rejected", EOutcome.NotExecuted, true, DateTimeOffset.UtcNow, Reason: reason);
                store.Save(rejected);
                return rejected;
            }
            ScriptPackageOperation accepted = new(request, fingerprint, "installing", EOutcome.Unknown, false, DateTimeOffset.UtcNow);
            string? runtime = request.Kind switch
            {
                EScriptPackageKind.Pip => runtimes?.SnapshotPath("python", request.PythonPath, current?.PythonPath),
                EScriptPackageKind.Npm => runtimes?.SnapshotPath("javascript", request.NodePath, current?.NodePath),
                _ => null,
            };
            store.Save(accepted);
            running = Task.Run(() => InstallAsync(accepted, packages.OrderBy(package => package.Id, StringComparer.Ordinal).ToArray(), current, runtime, stopping.Token));
            return accepted;
        }
    }

    /// <summary>停止時は所有するプロセスを終了させ、バックグラウンド処理を置き去りにしません。</summary>
    /// <returns>導入処理の停止確認です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名で、所有する取消トークン自体を終了します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        using (gate.EnterScope())
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        await stopping.CancelAsync().ConfigureAwait(false);
        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }
        stopping.Dispose();
    }

    /// <summary>復元に成功した新構成だけを公開し、失敗時は従来構成を維持します。</summary>
    /// <returns>永続化した最終結果です。</returns>
    // 抑制理由: 配布クライアントと外部インストーラーの最上位境界で失敗を記録し、受付済み操作を未監視Taskにしません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "バックグラウンド導入の最終状態を必ず管理記録へ返す境界です。")]
    private async Task InstallAsync(ScriptPackageOperation operation, IReadOnlyList<ScriptPackageReference> packages, ScriptPackageEnvironmentInfo? current, string? runtime, CancellationToken cancellationToken = default)
    {
        ScriptPackageOperation result;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            string? pythonVersion = null, pythonPath = null, nodeVersion = null, nodePath = null;
            if (operation.Request.Kind == EScriptPackageKind.Pip)
            {
                pythonPath = operation.Request.PythonPath ?? current?.PythonPath;
                if (runtimes is not null)
                {
                    pythonPath = await runtimes.ResolveAsync("python", runtime, cancellationToken: timeout.Token).ConfigureAwait(false);
                }
                pythonVersion = await PipScriptPackages.VersionAsync(pythonPath!, timeout.Token).ConfigureAwait(false);
            }
            if (operation.Request.Kind == EScriptPackageKind.Npm)
            {
                nodePath = operation.Request.NodePath ?? current?.NodePath;
                if (runtimes is not null)
                {
                    nodePath = await runtimes.ResolveAsync("javascript", runtime, cancellationToken: timeout.Token).ConfigureAwait(false);
                }
                nodeVersion = await JavaScriptRuntime.VersionAsync(nodePath!, timeout.Token).ConfigureAwait(false);
            }
            ScriptPackageEnvironment environment;
            ScriptPackageEnvironment? existing = store.FindRequested(operation.Request.Kind, packages, pythonPath, pythonVersion, nodePath, nodeVersion);
            if (existing is not null)
            {
                environment = existing;
            }
            else
            {
                string directory = store.Workspace(operation.Request.OperationId);
                switch (operation.Request.Kind)
                {
                    case EScriptPackageKind.Nuget:
                        environment = await NugetScriptPackages.InstallAsync(packages, directory, store.NugetCache, nugetSource, timeout.Token).ConfigureAwait(false);
                        break;
                    case EScriptPackageKind.Pip:
                        environment = await PipScriptPackages.InstallAsync(packages, pythonPath!, pythonVersion!, directory, wheelDirectory, timeout.Token).ConfigureAwait(false);
                        break;
                    default:
                        string? cli = operation.Request.NpmPath;
                        if (ScriptRuntimePaths.Comparer.Equals(nodePath, current?.NodePath))
                        {
                            cli ??= current?.NpmPath;
                        }
                        environment = await NpmScriptPackages.InstallAsync(packages, nodePath!, nodeVersion!, cli, directory, store.NpmCache, npmRegistry, timeout.Token).ConfigureAwait(false);
                        break;
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            environment = store.Find(environment.Info!.Revision) ?? environment;
            store.Publish(environment);
            result = operation with { Phase = "completed", Outcome = EOutcome.Succeeded, Completed = true, Revision = environment.Info!.Revision, UpdatedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception exception)
        {
            string reason = "script.packages.installFailed";
            string prefix = "script.packages.", runtimePrefix = "script.runtime.";
            string runtimeRequired = "script.javascriptRuntimeRequired";
            if (exception is InvalidDataException && (exception.Message.StartsWith(prefix, StringComparison.Ordinal) || string.Equals(exception.Message, runtimeRequired, StringComparison.Ordinal)))
            {
                reason = exception.Message;
            }
            if (exception is InvalidOperationException && exception.Message.StartsWith(runtimePrefix, StringComparison.Ordinal))
            {
                reason = exception.Message;
            }
            EOutcome outcome = EOutcome.Failed;
            string phase = "failed";
            if (exception is OperationCanceledException or Runtime.Processes.Commands.ProcessStopUnconfirmedException)
            {
                reason = "script.packages.interrupted";
                outcome = EOutcome.Unknown;
                phase = "interrupted";
            }
            result = operation with { Phase = phase, Outcome = outcome, Completed = true, Reason = reason, UpdatedAt = DateTimeOffset.UtcNow };
        }
        store.Save(result);
    }
}
