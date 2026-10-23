using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.Integration.Tests;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Clients;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>Installerの確認・実行を実Coreへ接続し、確認前に設定を変えないことを検証します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class ArchiveRestoreProcessTests
{
    /// <summary>復元先だけのWFの選択を、UDP経由の実復元にも反映します。</summary>
    /// <returns>保存版・有効版・保持または削除の確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InstallerPreviewsThenRestoresAnArchiveAgainstARealCore(int mode)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "one", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = core.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        await core.StartAsync(timeout.Token);
        PeerInfo? peer = null;
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await core.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
            peer = status.GetProperty("peers").Deserialize<PeerInfo[]>(ContractJson.Options)!.Single();
            return peer.Status == EPeerStatus.Ready;
        }, timeout.Token);
        WorkflowSaveResult original = await core.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), Definition("screen", 1), null), timeout.Token);
        Guid export = Guid.NewGuid();
        await core.PostAsync<ArchiveOperationResult>("archive.export", new ArchiveExportRequest(export), timeout.Token);
        ArchiveOperationResult? exported = null;
        await CorePeer.WaitAsync(async token =>
        {
            exported = await core.GetAsync<ArchiveOperationResult>("archive.operation", export, token).ConfigureAwait(false);
            return exported.Completed;
        }, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, exported!.Outcome);
        string archive = Path.Combine(core.DirectoryPath, "restore.zip");
        using (FileStream output = File.Create(archive))
        {
            await ArchiveFileTransfer.DownloadAsync(output, exported.File!, (operation, body, token) => core.GetAsync<JsonElement>(operation, body, token), timeout.Token);
        }
        WorkflowSaveResult changed = await core.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), Definition("screen", 2), original.Revision), timeout.Token);
        await core.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), Definition("extra", 3), null), timeout.Token);
        InstallationViewModel settings = new() { Operation = EInstallerOperation.Repair };
        settings.Repair.Mode = EInstallerRepairMode.Archive;
        settings.Repair.Archive.Path = archive;
        settings.Repair.Archive.ModeIndex = mode;
        ClientNetworkBinding binding = new(core.InterfaceId, "127.0.0.1", "127.255.255.255", port);
        settings.Selected.Add(new(cluster, "one", binding, peer));
        await InstallerArchive.PrepareAsync(settings, timeout.Token);
        ArchiveImportPreview preview = settings.Repair.Archive.Preview!;
        Assert.Equal(mode == 1, preview.Deleted.Count == 1);
        Assert.Equal(changed.Revision, (await core.GetAsync<JsonElement>("workflows.read", "screen", timeout.Token)).GetProperty("saved").GetProperty("revision").GetString());
        WizardResult result = await InstallerArchive.ExecuteAsync(settings, new Progress<OperationProgress>(), timeout.Token);
        Assert.Equal(WizardExecution.Outcome(settings, EOutcome.Succeeded), result.Summary);
        JsonElement restored = await core.GetAsync<JsonElement>("workflows.read", "screen", timeout.Token);
        Assert.Equal(original.Revision, restored.GetProperty("saved").GetProperty("revision").GetString());
        Assert.Equal(original.Revision, restored.GetProperty("effectiveRevision").GetString());
        WorkflowListPage list = await core.GetAsync<WorkflowListPage>("workflows.list", timeout.Token);
        Assert.Equal(mode == 0, list.Entries.Any(entry => entry.Id is "extra"));
    }

    /// <summary>外部副作用のない定義で、復元した版を区別します。</summary>
    /// <returns>値だけが異なるWFです。</returns>
    private static WorkflowDefinition Definition(string id, int value) => new(id, id, [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) })], [], Enabled: true);
}
