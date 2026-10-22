using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>管理済みWebクライアント更新の、削除可能な資材と保持境界を検証します。</summary>
public sealed class ManagedClientRetentionTests
{
    /// <summary>3世代・24時間を保護し、失敗・稼働中・別配置を残して旧資材だけを回収します。</summary>
    /// <returns>整理対象と保持対象の実ファイル確認です。</returns>
    [Fact]
    public async Task CleanupKeepsRecentGenerationsAndAllOperationRecords()
    {
        using ManagedClientRetentionFixture fixture = new();
        List<WebClientUpdateJob> jobs = [];
        for (int index = 0; index < 9; index++)
        {
            WebClientUpdateJob job = await fixture.AddAsync(index);
            jobs.Add(job);
            switch (index)
            {
                case 5:
                    await fixture.SaveAsync(job.OperationId, "result.json", fixture.Result(job) with { Outcome = EOutcome.Failed });
                    break;
                case 6:
                    await fixture.SaveAsync(job.OperationId, "writer.json", new WebClientUpdateWriter(job.OperationId, ContractJson.Hash(job), ProcessOs.CurrentIdentity()));
                    break;
                case 7:
                    await fixture.SaveAsync(job.OperationId, "job.json", job with { Installation = job.Installation with { InstallationId = Guid.NewGuid() } });
                    break;
                case 8:
                    File.SetLastWriteTimeUtc(Path.Combine(fixture.Operation(job.OperationId), "result.json"), DateTime.UtcNow);
                    break;
            }
        }
        await UpdateRetention.TrimManagedClientAsync(fixture.Program);
        await UpdateRetention.TrimManagedClientAsync(fixture.Program);
        for (int index = 0; index < jobs.Count; index++)
        {
            Guid id = jobs[index].OperationId;
            Assert.All(fixture.Assets(id), asset => Assert.Equal(3 <= index, Directory.Exists(asset)));
            foreach (string name in new[] { "job.json", "result.json", "writer.json", "started" })
            {
                Assert.True(File.Exists(Path.Combine(fixture.Operation(id), name)));
            }
        }
        Assert.Equal("current program", await File.ReadAllTextAsync(Path.Combine(fixture.Program, fixture.Client.EntryPoint)));
        Assert.Equal("retained settings", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "config", "profile.json")));
    }

    /// <summary>成功・所有・終了のいずれかを確認できなければ古い資材も保持します。</summary>
    /// <returns>未確定操作の保護確認です。</returns>
    [Theory]
    [InlineData("missing-writer")]
    [InlineData("missing-started")]
    [InlineData("wrong-hash")]
    [InlineData("wrong-started")]
    [InlineData("foreign-result")]
    [InlineData("unfinished")]
    [InlineData("unknown")]
    [InlineData("foreign-client")]
    [InlineData("foreign-payload")]
    [InlineData("invalid-process")]
    [InlineData("invalid-json")]
    public async Task UnconfirmedOperationIsNeverCollected(string fault)
    {
        using ManagedClientRetentionFixture fixture = new();
        WebClientUpdateJob oldest = await fixture.AddAsync(0);
        for (int generation = 1; generation <= 3; generation++)
        {
            await fixture.AddAsync(generation);
        }
        Guid id = oldest.OperationId;
        string operation = fixture.Operation(id);
        SetupResult result = fixture.Result(oldest);
        switch (fault)
        {
            case "missing-writer": File.Delete(Path.Combine(operation, "writer.json")); break;
            case "missing-started": File.Delete(Path.Combine(operation, "started")); break;
            case "wrong-hash": await fixture.SaveAsync(id, "writer.json", new WebClientUpdateWriter(id, "different input", new(int.MaxValue, DateTimeOffset.UnixEpoch))); break;
            case "wrong-started": await File.WriteAllTextAsync(Path.Combine(operation, "started"), "different input"); break;
            case "foreign-result": await fixture.SaveAsync(id, "result.json", result with { OperationId = Guid.NewGuid() }); break;
            case "unfinished": await fixture.SaveAsync(id, "result.json", result with { Finished = false }); break;
            case "unknown": await fixture.SaveAsync(id, "result.json", result with { Outcome = EOutcome.Unknown }); break;
            case "foreign-client": await fixture.SaveAsync(id, "result.json", result with { WebClient = fixture.Client with { InstallationId = Guid.NewGuid() } }); break;
            case "foreign-payload":
                WebClientUpdateJob foreign = oldest with { PayloadRoot = fixture.Program };
                string foreignHash = ContractJson.Hash(foreign);
                await fixture.SaveAsync(id, "job.json", foreign);
                await fixture.SaveAsync(id, "writer.json", new WebClientUpdateWriter(id, foreignHash, new(int.MaxValue, DateTimeOffset.UnixEpoch)));
                await File.WriteAllTextAsync(Path.Combine(operation, "started"), foreignHash);
                break;
            case "invalid-process": await fixture.SaveAsync(id, "writer.json", new WebClientUpdateWriter(id, ContractJson.Hash(oldest), new(0, DateTimeOffset.UnixEpoch))); break;
            case "invalid-json": await File.WriteAllTextAsync(Path.Combine(operation, "result.json"), "{"); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        await UpdateRetention.TrimManagedClientAsync(fixture.Program);
        Assert.All(fixture.Assets(id), asset => Assert.True(Directory.Exists(asset)));
        Assert.True(File.Exists(Path.Combine(fixture.Program, fixture.Client.EntryPoint)));
    }

    /// <summary>成功が4回以上あっても、24時間以内の資材は削除しません。</summary>
    /// <returns>保持期間の確認です。</returns>
    [Fact]
    public async Task RecentFourthGenerationIsKept()
    {
        using ManagedClientRetentionFixture fixture = new();
        List<WebClientUpdateJob> jobs = [];
        for (int index = 0; index < 4; index++)
        {
            WebClientUpdateJob job = await fixture.AddAsync(index);
            File.SetLastWriteTimeUtc(Path.Combine(fixture.Operation(job.OperationId), "result.json"), DateTime.UtcNow.AddHours(-4 + index));
            jobs.Add(job);
        }
        await UpdateRetention.TrimManagedClientAsync(fixture.Program);
        Assert.All(jobs, job => Assert.All(fixture.Assets(job.OperationId), asset => Assert.True(Directory.Exists(asset))));
    }

    /// <summary>親に記録があっても、別の実行物の配置からは整理しません。</summary>
    /// <returns>別配置への削除を行わない確認です。</returns>
    [Fact]
    public async Task DifferentProgramCannotCollectInstallationAssets()
    {
        using ManagedClientRetentionFixture fixture = new();
        WebClientUpdateJob oldest = await fixture.AddAsync(0);
        for (int generation = 1; generation <= 3; generation++)
        {
            await fixture.AddAsync(generation);
        }
        string other = Path.Combine(fixture.Root, "other-program");
        Directory.CreateDirectory(other);
        Assert.Null(await WebClientCatalog.FindByProgramAsync(other));
        await UpdateRetention.TrimManagedClientAsync(other);
        Assert.All(fixture.Assets(oldest.OperationId), asset => Assert.True(Directory.Exists(asset)));
        Assert.Equal(fixture.Client, await WebClientCatalog.FindByProgramAsync(fixture.Program));
    }
}
