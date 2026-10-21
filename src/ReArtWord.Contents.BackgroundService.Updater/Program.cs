using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

namespace ReArtWord.Contents.BackgroundService.Updater;

/// <summary>installation外で一回の明示更新を実行する単独実行入口です。</summary>
internal static class Program
{
    /// <summary>固定ジョブを読み、最後の実確認結果を終了コードへ反映します。</summary>
    /// <returns>成功0、既知失敗1、入力不足2、結果不明3です。</returns>
    // 抑制理由: C#の実行入口はMain(string[])の固定名・固定引数であり、通常の非同期API規約を適用できません。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "C# entry pointの固定引数です。"), SuppressMessage("SourceQuality", "RAW601", Justification = "C# entry pointの固定名です。")]
    private static async Task<int> Main(string[] args)
    {
        string option = "--job";
        string[] setupOptions = ["--setup-job", "--setup-web-client", "--remove-web-client", "--secure-install"];
        string clusterOption = "--cluster-job";
        string installOption = "--verify-install";
        string restorationOption = "--confirm-restoration";
        string clientOption = "--client-job";
        if (args.Length != 2 || (!setupOptions.Contains(args[0], StringComparer.Ordinal) && !string.Equals(args[0], option, StringComparison.Ordinal) && !string.Equals(args[0], clusterOption, StringComparison.Ordinal) && !string.Equals(args[0], installOption, StringComparison.Ordinal) && !string.Equals(args[0], restorationOption, StringComparison.Ordinal) && !string.Equals(args[0], clientOption, StringComparison.Ordinal)) || !Path.IsPathFullyQualified(args[1]))
        {
            await Console.Error.WriteLineAsync("Usage: ReArtWord.Contents.BackgroundService.Updater --job <absolute-json-path>").ConfigureAwait(false);
            return 2;
        }
        try
        {
            switch (args[0])
            {
                case "--setup-job": return await SetupRunner.RunAsync(args[1]).ConfigureAwait(false);
                case "--setup-web-client": return await SetupRunner.InstallClientAsync(args[1]).ConfigureAwait(false);
                case "--remove-web-client": return await SetupRunner.RemoveClientAsync(args[1]).ConfigureAwait(false);
                case "--secure-install": return await InstallSecurity.RunAsync(args[1]).ConfigureAwait(false);
            }
            if (string.Equals(args[0], clientOption, StringComparison.Ordinal))
            {
                ClientUpdateJob job = JsonSerializer.Deserialize<ClientUpdateJob>(await File.ReadAllTextAsync(args[1]).ConfigureAwait(false), ContractJson.Options)
                    ?? throw new InvalidDataException("ClientHost job is missing.");
                ClientUpdateProgress progress = await ClientUpdateExecutor.ExecuteAsync(job).ConfigureAwait(false);
                if (progress.Outcome == EOutcome.Succeeded)
                {
                    return 0;
                }
                return 3;
            }
            if (string.Equals(args[0], installOption, StringComparison.Ordinal))
            {
                return await InstallVerification.RunAsync(args[1]).ConfigureAwait(false);
            }
            if (string.Equals(args[0], clusterOption, StringComparison.Ordinal))
            {
                return await RunClusterAsync(args[1]).ConfigureAwait(false);
            }
            UpdatePeerResult result;
            if (string.Equals(args[0], restorationOption, StringComparison.Ordinal))
            {
                result = await RestorationVerification.RunAsync(args[1]).ConfigureAwait(false);
            }
            else
            {
                LocalUpdateJob job = JsonSerializer.Deserialize<LocalUpdateJob>(await File.ReadAllTextAsync(args[1]).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("更新jobがありません。");
                result = await LocalUpdateExecutor.ExecuteAsync(job).ConfigureAwait(false);
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ContractJson.Options)).ConfigureAwait(false);
            if (result.Outcome == EOutcome.Succeeded)
            {
                return 0;
            }
            if (result.Outcome == EOutcome.Unknown)
            {
                return 3;
            }
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("更新入力または耐久状態を確認できません。操作は自動再実行しません。").ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>更新される全サービスの寿命から独立して固定操作を完了します。</summary>
    /// <returns>最終状態に対応する終了コードです。</returns>
    private static async Task<int> RunClusterAsync(string path, CancellationToken cancellationToken = default)
    {
        ClusterUpdateJob job = JsonSerializer.Deserialize<ClusterUpdateJob>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("Cluster jobがありません。");
        UdpUpdatePeerTransport transport = new(job, path);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = transport.ConfigureAwait(false);
        IDurableRepository repository;
        string json = "json";
        string sqlite = "sqlite";
        if (string.Equals(job.StorageBackend, json, StringComparison.Ordinal))
        {
            repository = new JsonFileRepository(job.RepositoryPath);
        }
        else
        {
            if (!string.Equals(job.StorageBackend, sqlite, StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存媒体が不明です。");
            }
            repository = new SqliteRepository(job.RepositoryPath);
        }
        ClusterUpdateStatus status = await new ClusterUpdateCoordinator(repository, transport, job.CoordinatorPeerId).ExecuteAsync(job.Request, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(status, ContractJson.Options)).ConfigureAwait(false);
        if (status.Outcome == EOutcome.Succeeded)
        {
            return 0;
        }
        if (status.Outcome == EOutcome.Unknown)
        {
            return 3;
        }
        return 1;
    }
}
