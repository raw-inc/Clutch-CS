using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>配置操作の入力固定と最終結果を保存し、同じ副作用を再実行しません。</summary>
public sealed class SetupOperationJournal(Guid operationId, string directory, string root)
{
    /// <summary>既に始まった操作は記録された結果だけを返します。</summary>
    /// <returns>保存結果、入力不一致、途中停止、または未開始のnullです。</returns>
    public async Task<SetupResult?> ExistingAsync<T>(T job, CancellationToken cancellationToken = default)
    {
        string started = Path.Combine(directory, "started");
        if (!File.Exists(started))
        {
            return null;
        }
        if (!string.Equals(await File.ReadAllTextAsync(started, cancellationToken).ConfigureAwait(false), ContractJson.Hash(job), StringComparison.Ordinal))
        {
            return new(operationId, EOutcome.NotExecuted, "identity", "This operation ID was already used with different settings.", root);
        }
        string result = Path.Combine(directory, "result.json");
        if (File.Exists(result))
        {
            return JsonSerializer.Deserialize<SetupResult>(await File.ReadAllTextAsync(result, cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
        }
        return new(operationId, EOutcome.Unknown, "interrupted", "This operation has already started. Inspect the retained journal before creating a new operation.", root);
    }

    /// <summary>一度だけ開始の意図を保存します。</summary>
    /// <returns>入力の保存完了です。</returns>
    public async Task BeginAsync<T>(T job, CancellationToken cancellationToken = default)
    {
        using FileStream once = new(Path.Combine(directory, "started"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await once.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ContractJson.Hash(job)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>画面と結果照会へ返す確定結果を保存します。</summary>
    /// <returns>保存した結果です。</returns>
    public async Task<SetupResult> CompleteAsync(SetupResult result, CancellationToken cancellationToken = default)
    {
        await InstallationFiles.WriteAsync(Path.Combine(directory, "result.json"), result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
