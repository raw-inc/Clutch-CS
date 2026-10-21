using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates;

/// <summary>同じ利用者の独立Updaterへ一回を引き渡し、記録後にClientHostを正常終了します。</summary>
internal sealed class ClientUpdateLauncher(ClientHostOptions options, IHostApplicationLifetime lifetime)
{
    /// <summary>インストール済み配布だけを対象に、同じ利用者の旧更新実行物を整理します。</summary>
    /// <returns>記録を保持した一回の世代整理です。</returns>
    internal async Task TrimAsync(CancellationToken cancellationToken = default)
    {
        string installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!File.Exists(Path.Combine(installation, LocalUpdateExecutor.ManifestFileName)))
        {
            return;
        }
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(installation, cancellationToken).ConfigureAwait(false);
        ClientUpdateExecutor.ValidateRelease(manifest);
        await UpdateRetention.TrimClientAsync(new(Guid.NewGuid(), manifest, installation, options.StateDirectory, options.Listen, Health.Process), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>実行中assemblyの版とOS実体です。</summary>
    internal static ClientUpdateHealth Health => ClientUpdateFiles.Health(typeof(ClientUpdateLauncher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    /// <summary>Coreへ更新を配送する前にClientHostの全資材・退避先・helperを準備します。</summary>
    /// <returns>入替をまだ実行しない準備です。</returns>
    internal async Task PrepareAsync(FleetUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Release.Client is not { } release)
        {
            return;
        }
        Validate(request.Release);
        ClientUpdateJob job = new(request.OperationId, release.Manifest, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), options.StateDirectory, options.Listen, Health.Process);
        ClientUpdateFiles files = new(job);
        if (Directory.Exists(files.Operation) || Directory.Exists(files.Candidate) || Directory.Exists(files.Backup))
        {
            throw new InvalidOperationException("This ClientHost update already has preparation files. Inspect the saved operation.");
        }
        ReleaseManifest previous = await LocalUpdateExecutor.ReadManifestAsync(files.Installation, cancellationToken).ConfigureAwait(false);
        ClientUpdateExecutor.ValidateRelease(previous);
        await ReleaseVerifier.VerifyAsync(previous, files.Installation, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(files.Operation);
        string source = Path.Combine(request.Release.PayloadRoot, release.RelativePayloadPath.Replace('/', Path.DirectorySeparatorChar));
        await ReleaseVerifier.CopyAsync(release.Manifest, source, files.Candidate, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(release.Manifest, files.Candidate, cancellationToken).ConfigureAwait(false);

        // 新配布に含まれる検証済みUpdaterを使い、旧ClientHostにhelperがない初回も同じ契約で準備します。
        _ = await BundledUpdater.ResolveAsync(files.Candidate, Path.Combine(files.Operation, "helper"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The ClientHost release must contain its verified updater.");
        await File.WriteAllTextAsync(files.JobPath, JsonSerializer.Serialize(job, ContractJson.Options), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同じ配布root内の同一版ClientHostだけを固定計画に含めます。</summary>
    internal static void Validate(FleetReleaseSelection selection)
    {
        if (selection.Client is not { } client)
        {
            return;
        }
        ClientUpdateExecutor.ValidateRelease(client.Manifest);
        if (!ReleaseVerifier.IsRelativePath(client.RelativePayloadPath)
            || !string.Equals(client.Manifest.ReleaseId, selection.Manifest.ReleaseId, StringComparison.Ordinal)
            || !string.Equals(client.Manifest.ExpectedBuildVersion, selection.Manifest.ExpectedBuildVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("ClientHost must belong to the selected release and payload root.");
        }
    }

    /// <summary>全Core成功後、helperの受付を確認してから配信Hostを正常終了します。</summary>
    /// <returns>耐久記録済みの引渡しです。</returns>
    internal async Task StartAsync(FleetUpdateStatus status, Func<FleetUpdateStatus, CancellationToken, Task> save, CancellationToken cancellationToken = default)
    {
        ClientUpdateJob job = new(status.Request.OperationId, status.Request.Release.Client!.Manifest,
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), options.StateDirectory, options.Listen, Health.Process);
        ClientUpdateFiles files = new(job);
        ClientUpdateJob saved = JsonSerializer.Deserialize<ClientUpdateJob>(await File.ReadAllTextAsync(files.JobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("ClientHost update job is missing.");
        if (!string.Equals(ContractJson.Hash(job), ContractJson.Hash(saved), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The prepared ClientHost update changed.");
        }
        string executable = await BundledUpdater.ResolveAsync(files.Candidate, Path.Combine(files.Operation, "helper"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("ClientHost updater is missing.");
        ClientUpdateProgress starting = new("Launching", false, EOutcome.Unknown, null, DateTimeOffset.UtcNow);
        await save(status with { Completed = false, Outcome = EOutcome.Unknown, Client = starting }, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Operation };
        start.ArgumentList.Add("--client-job");
        start.ArgumentList.Add(files.JobPath);
        using Process process = Process.Start(start) ?? throw new IOException("ClientHost updater did not start.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!process.HasExited)
        {
            ClientUpdateProgress? progress = await ClientUpdateFiles.ReadAsync(options.StateDirectory, job.OperationId, timeout.Token).ConfigureAwait(false);
            string ready = "WaitingForExit";
            if (progress?.Worker?.ProcessId == process.Id && string.Equals(progress.State, ready, StringComparison.Ordinal))
            {
                await save(status with { Completed = false, Outcome = EOutcome.Unknown, Client = progress with { State = "Updating" } }, cancellationToken).ConfigureAwait(false);
                lifetime.StopApplication();
                return;
            }
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        throw new IOException("ClientHost updater exited before accepting the saved operation.");
    }
}
