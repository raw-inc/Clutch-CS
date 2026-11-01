using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>固定した配置の変更を行い、途中失敗の段階を保存します。</summary>
public sealed class SetupExecutor
{
    private readonly IInstallationPlatform platform;

    /// <summary>OS境界を受け取ります。</summary>
    public SetupExecutor(IInstallationPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        this.platform = platform;
    }

    /// <summary>同じjobを暗黙再実行せず、所有記録を検証して変更します。</summary>
    /// <returns>成功、失敗または中断した段階です。</returns>
    public async Task<SetupResult> ExecuteAsync(SetupJob job, string journalDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        SetupJob owned = Contracts.Serialization.ContractJson.Copy(job);
        string journal = InstallationFiles.Root(journalDirectory);
        Directory.CreateDirectory(journal);
        SetupOperationJournal operation = new(owned.OperationId, journal, owned.Settings.Root);
        SetupResult? previous = await operation.BeginAsync(owned, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            return previous;
        }
        SetupProgressJournal progress = new(owned.Action, journal, owned.Action == ESetupAction.Install && owned.Settings.WebClient is not null);
        try
        {
            await progress.BeginAsync("validate", cancellationToken).ConfigureAwait(false);
            ArgumentOutOfRangeException.ThrowIfEqual(owned.OperationId, Guid.Empty);
            string root = InstallationFiles.Root(owned.Settings.Root);
            if (UpdateLayout.Contains(root, journal))
            {
                throw new InvalidDataException("The operation journal must be outside the installation.");
            }
            if (!Enum.IsDefined(owned.Action))
            {
                throw new InvalidDataException("Unsupported setup action.");
            }
            InstallationRecord? existing = null;
            if (owned.Action != ESetupAction.Install)
            {
                existing = await InstallationFiles.ReadAsync(root, cancellationToken).ConfigureAwait(false);
            }
            using FileStream? installationLock = AcquireLock(existing);
            InstallationRecord record;
            if (owned.Action == ESetupAction.Install)
            {
                InstallationSettings selected = await new WebClientInstaller(platform).ResolveAsync(owned.Settings, cancellationToken).ConfigureAwait(false);
                InstallationPolicy.Validate(selected);
                if (!InstallationPolicy.CanInstallInto(root))
                {
                    throw new IOException("The destination already exists. Select Repair or a new folder.");
                }
                await progress.BeginAsync("verify-package", cancellationToken).ConfigureAwait(false);
                record = await PrepareRecordAsync(selected, cancellationToken: cancellationToken).ConfigureAwait(false);
                PrepareDirectories(record, Directory.Exists(root));
                await InstallationFiles.CreateRecordAsync(record, cancellationToken).ConfigureAwait(false);
                await progress.BeginAsync("copy", cancellationToken).ConfigureAwait(false);
                await CopyPackageAsync(record, cancellationToken).ConfigureAwait(false);
                if (record.Settings.WebClient is not null)
                {
                    await progress.BeginAsync("web-client", cancellationToken).ConfigureAwait(false);
                    record = record with { WebClient = await new WebClientInstaller(platform).InstallAsync(record, cancellationToken).ConfigureAwait(false) };
                }
                await progress.BeginAsync("configure", cancellationToken).ConfigureAwait(false);
                await WriteConfigurationAsync(record, false, cancellationToken).ConfigureAwait(false);
                await progress.BeginAsync("recovery", cancellationToken).ConfigureAwait(false);
                string recovery = Path.Combine(root, "recovery");
                await CopyPackageAsync(record with { Settings = record.Settings with { Root = recovery } }, cancellationToken).ConfigureAwait(false);
                record = record with { Settings = record.Settings with { PackageRoot = recovery } };
                await InstallationFiles.WriteAsync(Path.Combine(root, InstallationFiles.RecordName), record, cancellationToken).ConfigureAwait(false);
                await ClientLaunchFiles.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                await progress.BeginAsync("register", cancellationToken).ConfigureAwait(false);
                await platform.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                record = existing!;
                if (record.InstallationId != owned.InstallationId)
                {
                    throw new InvalidDataException("The selected installation has changed. Scan again.");
                }
                if (owned.Action == ESetupAction.Reinstall)
                {
                    if (!owned.KeepDatabase && !owned.SurvivorConfirmed)
                    {
                        throw new InvalidDataException("Resynchronization needs another Ready peer in the cluster.");
                    }
                    await progress.BeginAsync("verify-package", cancellationToken).ConfigureAwait(false);
                    _ = await PrepareRecordAsync(record.Settings with { PackageRoot = owned.Settings.PackageRoot }, record.ClientEntryPoint, cancellationToken).ConfigureAwait(false);
                    await InstallationRecovery.ValidateVersionAsync(record, owned.Settings.PackageRoot, cancellationToken).ConfigureAwait(false);
                    if (!owned.KeepDatabase)
                    {
                        await InstallationReadiness.ConfirmSurvivorAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (owned.Action == ESetupAction.Uninstall && record.Settings.WebClient is null)
                {
                    string executable = Path.Combine(record.Settings.Root, "client", record.ClientEntryPoint);
                    if (File.Exists(executable))
                    {
                        _ = await new WebClientInstaller(platform).RetainAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                }
                await progress.BeginAsync("stop", cancellationToken).ConfigureAwait(false);
                if (owned.ServeResult)
                {
                    await platform.StopAsync(record, owned.PreviousProcess,
                        token => InstallationFiles.WriteTextAsync(Path.Combine(journal, "stop.request"), Contracts.Serialization.ContractJson.Hash(owned), token), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await platform.StopAsync(record, owned.PreviousProcess, cancellationToken).ConfigureAwait(false);
                }
                if (owned.Action == ESetupAction.Uninstall)
                {
                    await progress.BeginAsync("unregister", cancellationToken).ConfigureAwait(false);
                    await platform.UnregisterAsync(record, cancellationToken).ConfigureAwait(false);
                    await progress.BeginAsync("remove", cancellationToken).ConfigureAwait(false);
                    await UninstallAsync(record, owned, cancellationToken).ConfigureAwait(false);
                    await progress.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    return await operation.CompleteAsync(new(owned.OperationId, EOutcome.Succeeded, "uninstalled", "The selected service was removed. Data and configuration retention followed the selected options.", root), cancellationToken).ConfigureAwait(false);
                }
                if (owned.Action != ESetupAction.Reinstall)
                {
                    throw new InvalidDataException("Unsupported setup action.");
                }
                PrepareDirectories(record, true);
                await progress.BeginAsync("backup", cancellationToken).ConfigureAwait(false);
                await ReinstallAsync(record, owned, progress, cancellationToken).ConfigureAwait(false);
                await progress.BeginAsync("register", cancellationToken).ConfigureAwait(false);
                await platform.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
                await new WebClientInstaller(platform).RefreshStartupAsync(record, cancellationToken).ConfigureAwait(false);
            }
            await progress.BeginAsync("start", cancellationToken).ConfigureAwait(false);
            await platform.StartAsync(record, cancellationToken).ConfigureAwait(false);
            await progress.BeginAsync("ready", cancellationToken).ConfigureAwait(false);
            await platform.VerifyAsync(record, cancellationToken).ConfigureAwait(false);
            await progress.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return await operation.CompleteAsync(new(owned.OperationId, EOutcome.Succeeded, "ready", "The installed peer reached Ready.", root, WebClient: record.WebClient), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException or System.Text.Json.JsonException or FormatException)
        {
            EOutcome outcome = EOutcome.Failed;
            if (exception is OperationCanceledException)
            {
                outcome = EOutcome.Unknown;
            }
            return await operation.CompleteAsync(new(owned.OperationId, outcome, progress.Current.Stage, exception.Message, owned.Settings.Root), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>実行物は読取り専用にし、サービスの保存データには一般ユーザーの権限を付けません。</summary>
    private void PrepareDirectories(InstallationRecord record, bool existing)
    {
        string root = record.Settings.Root;
        bool perUser = record.Settings.PerUser;
        platform.PrepareDirectory(root, perUser);
        foreach (string directory in new[] { "data", "logs", "workspace", "staging", "backups", "client-state" })
        {
            platform.PrepareDirectory(Path.Combine(root, directory), perUser, privateData: true, recursive: existing);
        }
        platform.PrepareDirectory(Path.Combine(root, "config"), perUser, recursive: existing);
        foreach (string directory in Components(record.Settings).Append("recovery"))
        {
            if (existing && Directory.Exists(Path.Combine(root, directory)))
            {
                platform.PrepareDirectory(Path.Combine(root, directory), perUser, recursive: true);
            }
        }
    }

    /// <summary>開始から最終起動確認まで既存配置の変更を一操作へ限定します。</summary>
    /// <returns>所有するlock、初回導入ならnullです。</returns>
    private static FileStream? AcquireLock(InstallationRecord? record)
    {
        if (record is null)
        {
            return null;
        }
        return new FileStream(Path.Combine(record.Settings.Root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>全componentを変更前に検証します。</summary>
    /// <returns>導入する固定実行物の記録です。</returns>
    private static async Task<InstallationRecord> PrepareRecordAsync(InstallationSettings settings, string? clientEntryPoint = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ReleaseManifest> manifests = new(StringComparer.Ordinal);
        foreach (string component in Components(settings))
        {
            string path = Path.Combine(settings.PackageRoot, component);
            ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(manifest.Platform, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(manifest.ExpectedBuildVersion))
            {
                throw new InvalidDataException("The package does not match this operating system / architecture.");
            }
            await ReleaseVerifier.VerifyAsync(manifest, path, cancellationToken).ConfigureAwait(false);
            manifests.Add(component, manifest);
        }
        if (clientEntryPoint is null)
        {
            ReleaseManifest client = await LocalUpdateExecutor.ReadManifestAsync(Path.Combine(settings.PackageRoot, "client"), cancellationToken).ConfigureAwait(false);
            clientEntryPoint = client.EntryPoint;
            if (settings.WebClient is { Install: true })
            {
                await ReleaseVerifier.VerifyAsync(client, Path.Combine(settings.PackageRoot, "client"), cancellationToken).ConfigureAwait(false);
            }
        }
        Guid identity = Guid.NewGuid();
        return new(identity, settings, "Clutch-CS-" + identity.ToString("N"), manifests["core"].EntryPoint, manifests["updater"].EntryPoint, clientEntryPoint, DateTimeOffset.UtcNow);
    }

    /// <summary>manifestにあるcomponentだけを配置します。</summary>
    /// <returns>検証済み配置です。</returns>
    private static async Task CopyPackageAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        foreach (string component in Components(record.Settings))
        {
            string source = Path.Combine(record.Settings.PackageRoot, component);
            string target = Path.Combine(record.Settings.Root, component);
            ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(source, cancellationToken).ConfigureAwait(false);
            await ReleaseVerifier.CopyAsync(manifest, source, target, cancellationToken).ConfigureAwait(false);
            await LocalUpdateExecutor.WriteManifestAsync(manifest, target, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>変更されるcomponentとDBを操作別backupへ退避します。</summary>
    /// <returns>配置再作成の完了です。</returns>
    private static async Task ReinstallAsync(InstallationRecord record, SetupJob job, SetupProgressJournal progress, CancellationToken cancellationToken = default)
    {
        string backup = Path.Combine(record.Settings.Root, "backups", "setup-" + job.OperationId.ToString("N"));
        Directory.CreateDirectory(InstallationFiles.Root(backup));
        foreach (string component in Components(record.Settings))
        {
            string source = InstallationFiles.Root(Path.Combine(record.Settings.Root, component));
            if (Directory.Exists(source))
            {
                await InstallationFiles.MoveDirectoryAsync(record.Settings.Root, source, Path.Combine(backup, component), cancellationToken).ConfigureAwait(false);
            }
        }
        if (!job.KeepDatabase)
        {
            string data = InstallationFiles.Root(Path.Combine(record.Settings.Root, "data"));
            if (Directory.Exists(data))
            {
                await InstallationFiles.MoveDirectoryAsync(record.Settings.Root, data, Path.Combine(backup, "data"), cancellationToken).ConfigureAwait(false);
            }
        }
        InstallationRecord replacement = record with { Settings = record.Settings with { PackageRoot = job.Settings.PackageRoot } };
        await progress.BeginAsync("copy", cancellationToken).ConfigureAwait(false);
        await CopyPackageAsync(replacement, cancellationToken).ConfigureAwait(false);
        await progress.BeginAsync("recovery", cancellationToken).ConfigureAwait(false);
        foreach (string component in Components(record.Settings))
        {
            string source = Path.Combine(job.Settings.PackageRoot, component);
            ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(source, cancellationToken).ConfigureAwait(false);
            await InstallationRecovery.ReplaceAsync(record.Settings.Root, component, source, manifest, cancellationToken).ConfigureAwait(false);
        }
        replacement = replacement with { Settings = replacement.Settings with { PackageRoot = Path.Combine(record.Settings.Root, "recovery") } };
        if (!job.KeepDatabase)
        {
            // 元TOMLの任意設定は保持し、空DBの単独初期化だけを禁止します。
            string config = Path.Combine(record.Settings.Root, "config", "contents.toml");
            string text = await File.ReadAllTextAsync(config, cancellationToken).ConfigureAwait(false);
            Tomlyn.Model.TomlTable table = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(text)!;
            ((Tomlyn.Model.TomlTable)table["cluster"])["initialize"] = false;
            await File.WriteAllTextAsync(config, Tomlyn.TomlSerializer.Serialize(table), cancellationToken).ConfigureAwait(false);
        }
        await InstallationFiles.WriteAsync(Path.Combine(record.Settings.Root, InstallationFiles.RecordName), replacement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>初期設定とクライアント表示資材を保存します。</summary>
    /// <returns>保存完了です。</returns>
    private static async Task WriteConfigurationAsync(InstallationRecord record, bool resynchronize, CancellationToken cancellationToken = default)
    {
        foreach (string directory in new[] { "config", "data", "logs", "workspace", "staging", "backups", "client-state" })
        {
            Directory.CreateDirectory(Path.Combine(record.Settings.Root, directory));
        }
        await File.WriteAllTextAsync(Path.Combine(record.Settings.Root, "config", "contents.toml"), InstallationConfiguration.Create(record, resynchronize), cancellationToken).ConfigureAwait(false);
        if (record.Settings.WebClient is null)
        {
            ClientAppearance appearance = await InstallationImages.CopyAsync(record, cancellationToken).ConfigureAwait(false);
            await InstallationFiles.WriteAsync(Path.Combine(record.Settings.Root, "config", "appearance.json"), appearance, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>登録されたcomponentだけを削除し、ワークスペースとログは保持します。</summary>
    /// <returns>明示した保持設定に従う削除の完了です。</returns>
    private static async Task UninstallAsync(InstallationRecord record, SetupJob job, CancellationToken cancellationToken = default)
    {
        bool retainedClient = File.Exists(Path.Combine(record.Settings.Root, "client", WebClientCatalog.RecordName));
        bool retainedClientConfiguration = File.Exists(Path.Combine(record.Settings.Root, "client", WebClientCatalog.RemovedRecordName));
        foreach (string directory in new[] { "core", "updater", "cli", "staging", "backups", "recovery" })
        {
            await InstallationFiles.DeleteDirectoryAsync(record.Settings.Root, directory, cancellationToken).ConfigureAwait(false);
        }
        if (record.Settings.WebClient is null && !retainedClient && !retainedClientConfiguration)
        {
            await InstallationFiles.DeleteDirectoryAsync(record.Settings.Root, "client", cancellationToken).ConfigureAwait(false);
        }
        if (!job.KeepDatabase)
        {
            await InstallationFiles.DeleteDirectoryAsync(record.Settings.Root, "data", cancellationToken).ConfigureAwait(false);
        }
        if (!job.KeepConfiguration)
        {
            if (retainedClient && record.Settings.WebClient is null)
            {
                await InstallationFiles.DeleteFileAsync(record.Settings.Root, Path.Combine("config", "contents.toml"), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InstallationFiles.DeleteDirectoryAsync(record.Settings.Root, "config", cancellationToken).ConfigureAwait(false);
            }
            await InstallationFiles.DeleteDirectoryAsync(record.Settings.Root, "client-state", cancellationToken).ConfigureAwait(false);
        }
        if (!retainedClient)
        {
            await InstallationFiles.DeleteFileAsync(record.Settings.Root, "Open-Client.cmd", cancellationToken).ConfigureAwait(false);
            await InstallationFiles.DeleteFileAsync(record.Settings.Root, "open-client.sh", cancellationToken).ConfigureAwait(false);
        }

        // 削除途中の中断を、削除完了の記録に置き換えません。
        await InstallationFiles.MoveFileAsync(record.Settings.Root, InstallationFiles.RecordName, "uninstalled.json", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>新形式ではPC共通のクライアントをCoreの再配置に含めません。</summary>
    /// <returns>この配置が所有する実行componentです。</returns>
    private static string[] Components(InstallationSettings settings)
    {
        if (settings.WebClient is null)
        {
            return ["core", "updater", "client", "cli"];
        }
        return ["core", "updater", "cli"];
    }

}
