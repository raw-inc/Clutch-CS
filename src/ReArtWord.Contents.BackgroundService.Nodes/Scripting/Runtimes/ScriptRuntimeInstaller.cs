using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>公式実行環境の追加導入を一度だけ所有し、成功後に既定を切り替えます。</summary>
public sealed class ScriptRuntimeInstaller : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly ScriptRuntimeRegistry registry;
    private readonly ScriptRuntimeReleases releases;
    private readonly HttpClient client;
    private readonly string records;
    private readonly Dictionary<Guid, ScriptRuntimeInstallOperation> operations = [];
    private readonly CancellationTokenSource stopping = new();
    private Task? running;
    private bool disposed;

    /// <summary>Peerの記録と、ログイン利用者ホームへの導入を結び付けます。</summary>
    // 抑制理由: HttpClientへHandlerの所有権を移し、この型のDisposeAsyncでまとめて解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "HttpClientがHandlerを所有し、InstallerがClientを破棄します。")]
    public ScriptRuntimeInstaller(string dataDirectory, ScriptRuntimeRegistry registry)
        : this(dataDirectory, registry, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan })
    {
    }

    /// <summary>隔離試験では同じHTTP経路をローカル配布物へ接続します。クライアントの所有権を受け取ります。</summary>
    internal ScriptRuntimeInstaller(string dataDirectory, ScriptRuntimeRegistry registry, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
        this.client = client;
        releases = new(client);
        records = Path.Combine(Path.GetFullPath(dataDirectory), "script-runtimes", "operations");
        Directory.CreateDirectory(records);
        foreach (string path in Directory.EnumerateFiles(records, "*.json"))
        {
            ScriptRuntimeInstallOperation operation = ScriptRecordFile.Read<ScriptRuntimeInstallOperation>(path)!;
            if (!operation.Completed)
            {
                operation = operation with { Completed = true, Phase = "interrupted", Outcome = EOutcome.Unknown, UpdatedAt = DateTimeOffset.UtcNow, Reason = "script.runtime.interrupted" };
                ScriptRecordFile.Write(path, operation);
            }
            operations[operation.Request.OperationId] = operation;
        }
    }

    /// <summary>現在のOSに対応する導入可能な安定版を取得します。</summary>
    /// <returns>公式カタログからの選択肢です。</returns>
    public Task<IReadOnlyList<ScriptRuntimeRelease>> ReleasesAsync(string language, CancellationToken cancellationToken = default) => releases.ReadAsync(language, cancellationToken);

    /// <summary>再取得によって導入を再送せず、進捗と直近の元操作を返します。</summary>
    /// <returns>最大20件の操作結果です。</returns>
    public ScriptRuntimeInstallStatus Status()
    {
        using (gate.EnterScope())
        {
            return new(running is { IsCompleted: false }, operations.Values.OrderByDescending(operation => operation.UpdatedAt).Take(20).ToArray());
        }
    }

    /// <summary>再送された識別子は同じ結果へ戻します。</summary>
    /// <returns>受付時と同じ操作、または未受付です。</returns>
    public ScriptRuntimeInstallOperation? Operation(Guid id)
    {
        using (gate.EnterScope())
        {
            return operations.GetValueOrDefault(id);
        }
    }

    /// <summary>利用者と既定設定版を受付時に固定し、導入完了を待たずに返します。</summary>
    /// <returns>受付または確定した拒否です。</returns>
    // 抑制理由: 導入Taskをrunningへ移し、DisposeAsyncで取消して終了を待ちます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有したTaskを停止時にawaitします。")]
    public ScriptRuntimeInstallOperation Start(ScriptRuntimeInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = ScriptRuntimePaths.Language(request.Language);
        if (request.OperationId == Guid.Empty || !Version.TryParse(request.Version, out Version? version) || version.Build < 0 || !string.Equals(version.ToString(), request.Version, StringComparison.Ordinal))
        {
            throw new ArgumentException("script.runtime.versionInvalid", nameof(request));
        }
        string fingerprint = ContractJson.Hash(request);
        using (gate.EnterScope())
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (operations.TryGetValue(request.OperationId, out ScriptRuntimeInstallOperation? existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new(request, fingerprint, "rejected", EOutcome.NotExecuted, true, DateTimeOffset.UtcNow, Reason: "script.runtime.operationMismatch");
                }
                return existing;
            }
            if (running is { IsCompleted: false })
            {
                ScriptRuntimeInstallOperation rejected = new(request, fingerprint, "rejected", EOutcome.NotExecuted, true, DateTimeOffset.UtcNow, Reason: "script.runtime.busy");
                Save(rejected);
                return rejected;
            }
            ScriptRuntimeUser? user = null;
            try
            {
                user = registry.User();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                // 未ログインや利用者情報へのアクセス失敗は、副作用のない受付拒否として残します。
            }
            if (user is null)
            {
                ScriptRuntimeInstallOperation rejected = new(request, fingerprint, "rejected", EOutcome.NotExecuted, true, DateTimeOffset.UtcNow, Reason: "script.runtime.userUnavailable");
                Save(rejected);
                return rejected;
            }
            string directory = ScriptRuntimePaths.InstallRoot(user, request.Language);
            ScriptRuntimeInstallOperation accepted = new(request, fingerprint, "accepted", EOutcome.Unknown, false, DateTimeOffset.UtcNow, user.Name, directory);
            string? revision = registry.Revision(request.Language);
            Save(accepted);
            running = Task.Run(() => InstallAsync(accepted, user, revision, stopping.Token));
            return accepted;
        }
    }

    /// <summary>未完了の取得・展開を止め、既存実行環境を残して元操作へ中断を記録します。</summary>
    /// <returns>所有処理の終了確認です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "自身が所有する取消元を終了します。")]
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
        releases.Dispose();
        client.Dispose();
        stopping.Dispose();
    }

    /// <summary>既存環境を上書きせず、検証と原子的公開の後でだけ既定変更を試みます。</summary>
    /// <returns>元の操作の確定状態です。</returns>
    // 抑制理由: 受付後の有限バックグラウンド操作の境界で、外部配布・OSの非致命的な失敗を結果へ保存します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "利用者へ元操作の失敗を返す最上位境界です。")]
    private async Task InstallAsync(ScriptRuntimeInstallOperation operation, ScriptRuntimeUser user, string? revision, CancellationToken cancellationToken = default)
    {
        string? workspace = null;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            IReadOnlyList<ScriptRuntimeRelease> available = await releases.ReadAsync(operation.Request.Language, timeout.Token).ConfigureAwait(false);
            ScriptRuntimeRelease release = available.FirstOrDefault(item => string.Equals(item.Version, operation.Request.Version, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("script.runtime.releaseUnavailable");
            release = await releases.WithHashAsync(release, timeout.Token).ConfigureAwait(false);
            string root = ScriptRuntimePaths.InstallRoot(user, release.Language);
            ScriptRuntimeArchive.EnsureRoot(user, root);
            using FileStream ownership = new(Path.Combine(root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string destination = Path.Combine(root, release.Version + "-" + release.Platform);
            string executableName = release.Language switch { "python" => "python.exe", _ => "node.exe" };
            string executable = Path.Combine(destination, executableName);
            operation = operation with { Directory = destination, ExecutablePath = executable };
            if (Directory.Exists(destination))
            {
                string marker = Path.Combine(destination, ".clutch-runtime.json");
                if (ScriptRecordFile.Read<ScriptRuntimeRelease>(marker) is not { } installed || installed != release)
                {
                    throw new InvalidOperationException("script.runtime.destinationExists");
                }
            }
            else
            {
                string candidate = Path.Combine(root, ".install-" + operation.Request.OperationId.ToString("N"));
                if (Directory.Exists(candidate))
                {
                    throw new InvalidOperationException("script.runtime.destinationExists");
                }
                Directory.CreateDirectory(candidate);
                workspace = candidate;
                string archive = Path.Combine(workspace, "runtime.zip"), staged = Path.Combine(workspace, "runtime");
                operation = Phase(operation, "downloading");
                await ScriptRuntimeArchive.DownloadAsync(client, release, archive, (received, total) => Progress(operation, received, total), timeout.Token).ConfigureAwait(false);
                operation = Phase(Operation(operation.Request.OperationId)!, "extracting");
                Directory.CreateDirectory(staged);
                await ScriptRuntimeArchive.ExtractAsync(archive, staged, release, timeout.Token).ConfigureAwait(false);
                operation = Phase(operation, "verifying");
                await VerifyAsync(release, Path.Combine(staged, executableName), timeout.Token).ConfigureAwait(false);
                ScriptRecordFile.Write(Path.Combine(staged, ".clutch-runtime.json"), release);
                timeout.Token.ThrowIfCancellationRequested();
                Directory.Move(staged, destination);
            }
            operation = Phase(operation, "verifying");
            await VerifyAsync(release, executable, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            bool applied = registry.CompleteInstall(release.Language, executable, revision);
            string? reason = null;
            if (!applied)
            {
                reason = "script.runtime.defaultChangedDuringInstall";
            }
            Save(operation with { Completed = true, Phase = "completed", Outcome = EOutcome.Succeeded, DefaultApplied = applied, Reason = reason, UpdatedAt = DateTimeOffset.UtcNow });
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            string reason = "script.runtime.installFailed", phase = "failed", prefix = "script.runtime.";
            EOutcome outcome = EOutcome.Failed;
            if (exception.Message.StartsWith(prefix, StringComparison.Ordinal))
            {
                reason = exception.Message;
            }
            if (exception is OperationCanceledException or ProcessStopUnconfirmedException)
            {
                reason = "script.runtime.interrupted";
                phase = "interrupted";
                outcome = EOutcome.Unknown;
            }
            Save(operation with { Completed = true, Phase = phase, Outcome = outcome, Reason = reason, UpdatedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            if (workspace is not null && Directory.Exists(workspace))
            {
                try
                {
                    Directory.Delete(workspace, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // この操作専用のステージを残し、次の操作が再使用しないようにします。
                }
            }
        }
    }

    /// <summary>言語の起動に加え、通常のパッケージ管理に必要な機能を確認します。</summary>
    /// <returns>正しい版とpipまたはnpmを利用できることの確認です。</returns>
    private static async Task VerifyAsync(ScriptRuntimeRelease release, string executable, CancellationToken cancellationToken = default)
    {
        ScriptRuntimeEntry entry = await ScriptRuntimePaths.ProbeAsync(release.Language, executable, "installed", false, cancellationToken).ConfigureAwait(false);
        if (!entry.Available || !string.Equals(entry.Version, release.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("script.runtime.versionMismatch");
        }
        IReadOnlyList<string> arguments;
        string python = "python";
        if (string.Equals(release.Language, python, StringComparison.Ordinal))
        {
            arguments = ["-I", "-S", "-c", "import venv, ensurepip; print(ensurepip.version())"];
        }
        else
        {
            arguments = [Path.Combine(Path.GetDirectoryName(executable)!, "node_modules", "npm", "bin", "npm-cli.js"), "--version"];
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await BoundedProcessRunner.RunAsync(executable, arguments, JavaScript.JavaScriptRuntime.Limits(4096, 536870912, executable: executable), timeout.Token).ConfigureAwait(false);
    }

    /// <summary>永続化は処理段階の境目だけに限定します。</summary>
    /// <returns>新しい段階の操作です。</returns>
    private ScriptRuntimeInstallOperation Phase(ScriptRuntimeInstallOperation operation, string phase)
    {
        operation = operation with { Phase = phase, UpdatedAt = DateTimeOffset.UtcNow };
        Save(operation);
        return operation;
    }

    /// <summary>頻繁なダウンロード進捗はメモリ上だけを更新します。</summary>
    private void Progress(ScriptRuntimeInstallOperation operation, long received, long? total)
    {
        using (gate.EnterScope())
        {
            operations[operation.Request.OperationId] = operation with { DownloadedBytes = received, TotalBytes = total, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>元の操作識別子で受付と確定結果を保持します。</summary>
    private void Save(ScriptRuntimeInstallOperation operation)
    {
        using (gate.EnterScope())
        {
            ScriptRecordFile.Write(Path.Combine(records, operation.Request.OperationId.ToString("N") + ".json"), operation);
            operations[operation.Request.OperationId] = operation;
        }
    }
}
