using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

namespace ReArtWord.Contents.BackgroundService.Updater;

/// <summary>元のジョブとオペレーターが確認した旧版の識別子を固定します。</summary>
internal static class RestorationVerification
{
    /// <summary>再起動を行わず、期限後の旧版復帰だけを確認します。</summary>
    /// <returns>元の操作に対する確認結果です。</returns>
    internal static async Task<UpdatePeerResult> RunAsync(string path, CancellationToken cancellationToken = default)
    {
        Input input = JsonSerializer.Deserialize<Input>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("旧版復帰の確認入力がありません。");
        if (!Path.IsPathFullyQualified(input.JobPath))
        {
            throw new InvalidDataException("元ジョブは絶対パスで指定してください。");
        }
        LocalUpdateJob job = JsonSerializer.Deserialize<LocalUpdateJob>(await File.ReadAllTextAsync(input.JobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("元ジョブがありません。");
        return await UpdateRestorationConfirmation.ConfirmAsync(job, input.ReleaseId, input.BuildVersion, input.InstanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>元ジョブと人が確認した実稼働の識別子です。</summary>
    // 抑制理由: JSON入力専用の型であり、System.Text.Jsonがコンストラクターを呼び出します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "System.Text.Jsonが確認入力から生成します。")]
    private sealed record Input(string JobPath, string ReleaseId, string BuildVersion, Guid InstanceId);
}
