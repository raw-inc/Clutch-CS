using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>更新世代整理が操作記録・直近世代・未確定・別配置を保護することを確認します。</summary>
public sealed class UpdateRetentionTests
{
    /// <summary>確定成功した古い実行物だけを削除し、再照会可能な記録を残します。</summary>
    /// <returns>実ファイル境界の確認です。</returns>
    [Fact]
    public async Task CoreCleanupPreservesRecentGenerationsAndUnconfirmedOperations()
    {
        using UpdateTestScope fixture = new();
        List<UpdateLayout> layouts = [];
        for (int index = 0; index < 9; index++)
        {
            Guid operation = Guid.NewGuid();
            UpdateLayout layout = new(fixture.Options, operation);
            layout.CreateDirectories();
            foreach (string directory in new[] { layout.PayloadRoot, layout.BackupPath, Path.Combine(layout.OperationRoot, "helper") })
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "owned.bin"), "owned");
            }
            ProcessIdentity writer = new(int.MaxValue, DateTimeOffset.UnixEpoch);
            EUpdatePhase phase = EUpdatePhase.Completed;
            UpdatesOptions options = fixture.Options;
            if (index == 5)
            {
                phase = EUpdatePhase.Interrupted;
            }
            if (index == 6)
            {
                writer = ProcessOs.CurrentIdentity();
            }
            if (index == 7)
            {
                options = options with { InstallationRoot = Path.Combine(fixture.Root, "other") };
            }
            LocalUpdateJob job = new(operation, "peer", Guid.NewGuid(), writer, new("test", "win-x64", "test.exe", [], []), layout.PayloadRoot, options, []);
            LocalUpdateRecord record = new(ContractJson.Hash(job), job, new(operation, "peer", phase, EOutcome.Succeeded), writer);
            await new JsonFileRepository(layout.JournalPath).WriteLocalSnapshotAsync("peer:peer:updates", "operation", NodeValue.FromObject(record));
            await File.WriteAllTextAsync(layout.JobPath, JsonSerializer.Serialize(job, ContractJson.Options));
            File.SetLastWriteTimeUtc(layout.JournalPath, DateTime.UtcNow.AddDays(-10).AddHours(index));
            if (index == 8)
            {
                File.SetLastWriteTimeUtc(layout.JournalPath, DateTime.UtcNow);
            }
            layouts.Add(layout);
        }
        await UpdateRetention.TrimCoreAsync(fixture.Options, "peer", Guid.NewGuid());
        for (int index = 0; index < layouts.Count; index++)
        {
            Assert.Equal(3 <= index, Directory.Exists(layouts[index].BackupPath));
            Assert.Equal(3 <= index, Directory.Exists(layouts[index].PayloadRoot));
            Assert.True(File.Exists(layouts[index].JournalPath));
            Assert.True(File.Exists(layouts[index].JobPath));
        }
    }

    /// <summary>ClientHostにも同じ成功・経過時間・世代の境界を適用します。</summary>
    /// <returns>別配置と記録を残した旧helperの回収です。</returns>
    [Fact]
    public async Task ClientCleanupKeepsJournalsAndDoesNotDeleteOtherInstallations()
    {
        using UpdateTestScope fixture = new();
        string state = Path.Combine(fixture.Root, "client-state");
        ClientUpdateJob current = new(Guid.NewGuid(), new("test", "win-x64", "client.exe", [], []), fixture.Options.InstallationRoot!, state,
            new Uri("http://127.0.0.1:5081"), new(int.MaxValue, DateTimeOffset.UnixEpoch));
        List<ClientUpdateFiles> files = [];
        for (int index = 0; index < 7; index++)
        {
            ClientUpdateJob job = current with { OperationId = Guid.NewGuid() };
            ClientUpdateFiles paths = new(job);
            Directory.CreateDirectory(paths.Operation);
            Directory.CreateDirectory(paths.Backup);
            Directory.CreateDirectory(Path.Combine(paths.Operation, "helper"));
            if (index == 5)
            {
                job = job with { InstallationRoot = Path.Combine(fixture.Root, "other") };
            }
            await File.WriteAllTextAsync(paths.JobPath, JsonSerializer.Serialize(job, ContractJson.Options));
            EOutcome outcome = EOutcome.Succeeded;
            if (index == 6)
            {
                outcome = EOutcome.Unknown;
            }
            ClientUpdateProgress result = new("Completed", true, outcome, current.PreviousProcess, DateTimeOffset.UtcNow.AddDays(-10).AddHours(index));
            await File.WriteAllTextAsync(Path.Combine(paths.Operation, "result.json"), JsonSerializer.Serialize(result, ContractJson.Options));
            files.Add(paths);
        }
        await UpdateRetention.TrimClientAsync(current);
        for (int index = 0; index < files.Count; index++)
        {
            Assert.Equal(2 <= index, Directory.Exists(files[index].Backup));
            Assert.Equal(2 <= index, Directory.Exists(Path.Combine(files[index].Operation, "helper")));
            Assert.True(File.Exists(files[index].JobPath));
            Assert.True(File.Exists(Path.Combine(files[index].Operation, "result.json")));
        }
    }
}
