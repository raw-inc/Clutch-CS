using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>管理済みクライアントと更新記録を、試験専用の一時配置に作ります。</summary>
internal sealed class ManagedClientRetentionFixture : IDisposable
{
    /// <summary>入れ子のプログラム配置でも導入記録を特定できる構成にします。</summary>
    internal ManagedClientRetentionFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "clutch-client-retention-" + Guid.NewGuid().ToString("N"));
        Client = new(Guid.NewGuid(), Root, "Clutch-CS.ClientHost.exe", 55300, true, ProgramDirectory: Path.Combine(Root, "programs", "app"));
        Directory.CreateDirectory(Program);
        File.WriteAllText(Path.Combine(Program, Client.EntryPoint), "current program");
        File.WriteAllText(Path.Combine(Root, WebClientCatalog.RecordName), JsonSerializer.Serialize(Client, ContractJson.Options));
        Directory.CreateDirectory(Path.Combine(Root, "config"));
        File.WriteAllText(Path.Combine(Root, "config", "profile.json"), "retained settings");
    }

    internal string Root
    {
        get;
    }

    internal WebClientInstallation Client
    {
        get;
    }

    internal string Program => Client.ProgramDirectory!;

    /// <summary>資材と成功記録を保存し、世代順をファイル時刻で固定します。</summary>
    /// <returns>試験が不確定状態へ変更できる入力です。</returns>
    internal async Task<WebClientUpdateJob> AddAsync(int generation, ProcessIdentity? writer = null, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        string operation = Operation(id);
        WebClientUpdateJob job = new(id, Client, new(int.MaxValue, DateTimeOffset.UnixEpoch),
            new("fixture", "win-x64", Client.EntryPoint, [], [], "fixture"), Path.Combine(operation, "payload"));
        foreach (string path in Assets(id))
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "asset.bin"), "old update material", cancellationToken).ConfigureAwait(false);
        }
        await SaveAsync(id, "job.json", job, cancellationToken).ConfigureAwait(false);
        string hash = ContractJson.Hash(job);
        await File.WriteAllTextAsync(Path.Combine(operation, "started"), hash, cancellationToken).ConfigureAwait(false);
        await SaveAsync(id, "writer.json", new WebClientUpdateWriter(id, hash, writer ?? new(int.MaxValue, DateTimeOffset.UnixEpoch)), cancellationToken).ConfigureAwait(false);
        await SaveAsync(id, "result.json", Result(job), cancellationToken).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(Path.Combine(operation, "result.json"), DateTime.UtcNow.AddDays(-10).AddHours(generation));
        return job;
    }

    /// <summary>入力と同じ導入に属する成功結果です。</summary>
    /// <returns>稼働確認済みの結果です。</returns>
    internal SetupResult Result(WebClientUpdateJob job) => new(job.OperationId, EOutcome.Succeeded, "ready", "confirmed", Root, WebClient: Client);

    /// <summary>操作記録の保存先です。</summary>
    /// <returns>試験root内の操作ディレクトリです。</returns>
    internal string Operation(Guid id) => Path.Combine(Root, "update-staging", "operations", id.ToString("N"));

    /// <summary>整理対象となる資材だけを列挙します。</summary>
    /// <returns>バックアップ・候補・配布物・helperの配置です。</returns>
    internal string[] Assets(Guid id) => [Program + ".backup-" + id.ToString("N"), Program + ".candidate-" + id.ToString("N"), Path.Combine(Operation(id), "payload"), Path.Combine(Operation(id), "helper")];

    /// <summary>記録の経過時間を維持して、各障害境界を作ります。</summary>
    /// <returns>記録の保存完了です。</returns>
    internal async Task SaveAsync<T>(Guid id, string name, T value, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Operation(id), name);
        DateTime timestamp = DateTime.UtcNow.AddDays(-10);
        if (File.Exists(path))
        {
            timestamp = File.GetLastWriteTimeUtc(path);
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, ContractJson.Options), cancellationToken).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(path, timestamp);
    }

    /// <summary>この試験が作成した一時配置だけを削除します。</summary>
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
