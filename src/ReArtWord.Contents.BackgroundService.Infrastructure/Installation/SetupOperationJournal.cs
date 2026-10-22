using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>配置操作の入力固定と最終結果を保存し、同じ副作用を再実行しません。</summary>
public sealed class SetupOperationJournal(Guid operationId, string directory, string root)
{
    /// <summary>既に始まった操作は記録された結果だけを返します。</summary>
    /// <returns>保存結果、入力不一致、未確定状態、または未開始のnullです。</returns>
    public async Task<SetupResult?> ExistingAsync<T>(T job, CancellationToken cancellationToken = default)
    {
        string started = Path.Combine(directory, "started");
        string resultPath = Path.Combine(directory, "result.json");
        try
        {
            string fingerprint;
            try
            {
                fingerprint = await ReadTextAsync(started, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return File.Exists(resultPath) ? Unconfirmed("The result has no matching start record.")
                                               : null;
            }
            if (fingerprint.Length != 64 || fingerprint.Any(character => !char.IsAsciiHexDigit(character)))
            {
                return Unconfirmed("The start record could not be confirmed.");
            }
            if (!string.Equals(fingerprint, ContractJson.Hash(job), StringComparison.Ordinal))
            {
                return new(operationId, EOutcome.NotExecuted, "identity", "This operation ID was already used with different settings.", root);
            }
            try
            {
                SetupResult? result = JsonSerializer.Deserialize<SetupResult>(await ReadTextAsync(resultPath, cancellationToken).ConfigureAwait(false), ContractJson.Options);
                if (result is null || result.OperationId != operationId || !Enum.IsDefined(result.Outcome))
                {
                    return Unconfirmed("The result does not identify this operation.");
                }
                return result;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return Unconfirmed("This operation has already started. Read its result instead of restarting it.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unconfirmed("The operation record could not be read: " + exception.Message);
        }
    }

    /// <summary>完成した開始記録を一度だけ公開し、競合した呼出元は既存状態の照会へ戻します。</summary>
    /// <returns>開始権を取得した場合だけnull、それ以外は書き換えずに取得した既存状態です。</returns>
    public async Task<SetupResult?> BeginAsync<T>(T job, CancellationToken cancellationToken = default)
    {
        SetupResult? previous = await ExistingAsync(job, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            return previous;
        }
        string started = Path.Combine(directory, "started");
        string temporary = started + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, ContractJson.Hash(job), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, started);
            }
            catch (IOException) when (File.Exists(started))
            {
                return await ExistingAsync(job, cancellationToken).ConfigureAwait(false)
                    ?? Unconfirmed("The competing start could not be confirmed.");
            }
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 作業ファイルを消せなくても、他の実行者の結果を失敗で上書きしません。
            }
        }
    }

    /// <summary>開始した実行者、またはhelper終了後の起動登録担当が確定結果を保存します。</summary>
    /// <returns>保存した結果です。</returns>
    public async Task<SetupResult> CompleteAsync(SetupResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.OperationId != operationId)
        {
            throw new InvalidDataException("The result belongs to another operation.");
        }
        await InstallationFiles.WriteAsync(Path.Combine(directory, "result.json"), result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>所有者の中断と実行中を断定せず、記録を変更しない結果を返します。</summary>
    /// <returns>結果照会を継続できる未確定状態です。</returns>
    private SetupResult Unconfirmed(string detail) => new(operationId, EOutcome.Unknown, "unconfirmed", detail, root, false);

    /// <summary>結果の原子的置換を妨げず、公開された記録だけを読みます。</summary>
    /// <returns>読み取った本文です。</returns>
    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
