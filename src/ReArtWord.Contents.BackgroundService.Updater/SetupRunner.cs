using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.Updater;

/// <summary>サービス自身を含む配置の変更を独立processで完了します。</summary>
internal static class SetupRunner
{
    /// <summary>Webクライアントと同じ利用者で、固定された一回の更新を実行します。</summary>
    /// <returns>成功0、失敗1、結果不明3です。</returns>
    public static async Task<int> UpdateClientAsync(string path, CancellationToken cancellationToken = default)
    {
        WebClientUpdateJob job = JsonSerializer.Deserialize<WebClientUpdateJob>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("Missing web client update.");
        SetupResult result = await new WebClientUpdateExecutor(new InstallationPlatform()).ExecuteAsync(job, Path.GetDirectoryName(path)!, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ContractJson.Options)).ConfigureAwait(false);
        return result.Outcome switch { EOutcome.Succeeded => 0, EOutcome.Unknown => 3, _ => 1 };
    }

    /// <summary>Coreとは別の権限でクライアントだけを導入します。</summary>
    /// <returns>成功0、失敗1です。</returns>
    public static async Task<int> InstallClientAsync(string path, CancellationToken cancellationToken = default)
    {
        WebClientSetupJob job = JsonSerializer.Deserialize<WebClientSetupJob>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("Missing web client operation.");
        SetupResult result = await new WebClientSetup(new InstallationPlatform()).ExecuteAsync(job, Path.GetDirectoryName(path)!, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ContractJson.Options)).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Succeeded)
        {
            return 0;
        }
        return 1;
    }

    /// <summary>クライアントだけの削除をサービス保守と分離して実行します。</summary>
    /// <returns>成功0、失敗1です。</returns>
    public static async Task<int> RemoveClientAsync(string path, CancellationToken cancellationToken = default)
    {
        WebClientRemovalJob job = JsonSerializer.Deserialize<WebClientRemovalJob>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("Missing web client operation.");
        SetupResult result = await new WebClientRemoval(new InstallationPlatform()).ExecuteAsync(job, Path.GetDirectoryName(path)!, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ContractJson.Options)).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Succeeded)
        {
            return 0;
        }
        return 1;
    }

    /// <summary>遠隔呼出にはCore停止後も結果を返します。</summary>
    /// <returns>成功0、失敗1です。</returns>
    public static async Task<int> RunAsync(string path, CancellationToken cancellationToken = default)
    {
        SetupJob job = JsonSerializer.Deserialize<SetupJob>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("Missing setup job.");
        string workspace = Path.GetDirectoryName(path)!;
        SetupResult result;
        if (job.ServeResult)
        {
            result = await RunRemoteAsync(job, workspace, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await new SetupExecutor(new InstallationPlatform()).ExecuteAsync(job, workspace, cancellationToken).ConfigureAwait(false);
        }
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ContractJson.Options)).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Succeeded)
        {
            return 0;
        }
        return 1;
    }

    /// <summary>削除後の結果通知を一時ルールで維持し、通知期間の終了時に回収します。</summary>
    /// <returns>保存済みの保守結果です。</returns>
    private static async Task<SetupResult> RunRemoteAsync(SetupJob job, string workspace, CancellationToken cancellationToken = default)
    {
        InstallationRecord? resultNetwork = null;
        try
        {
            if (OperatingSystem.IsWindows() && job.Action == ESetupAction.Uninstall)
            {
                resultNetwork = await InstallationFiles.ReadAsync(job.Settings.Root, cancellationToken).ConfigureAwait(false);
                await WindowsFirewall.ConfigureAsync(resultNetwork, false, job.OperationId, cancellationToken).ConfigureAwait(false);
            }
            SetupResultChannel channel = new(job.Settings.Network, job.Settings.ClusterId, job.OperationId, true);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = channel.ConfigureAwait(false);
            SetupResult? response = null;
            channel.Serve(() => response);
            SetupResult result = await new SetupExecutor(new InstallationPlatform()).ExecuteAsync(job, workspace, cancellationToken).ConfigureAwait(false);
            response = result;
            await Task.Delay(InstallationDefaults.ResultRetention, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (OperatingSystem.IsWindows() && resultNetwork is not null)
            {
                await WindowsFirewall.ConfigureAsync(resultNetwork, true, job.OperationId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
