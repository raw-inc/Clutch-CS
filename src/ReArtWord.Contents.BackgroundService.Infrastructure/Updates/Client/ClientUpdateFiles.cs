using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;

/// <summary>ClientHost入替の固定パスと、再起動をまたぐ一回の記録を管理します。</summary>
public sealed class ClientUpdateFiles
{
    private readonly FileAccessPolicy policy;

    /// <summary>入替対象と保存先の重複、root全体、reparse pointを拒否します。</summary>
    public ClientUpdateFiles(ClientUpdateJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.OperationId == Guid.Empty || !Path.IsPathFullyQualified(job.InstallationRoot) || !Path.IsPathFullyQualified(job.StateDirectory)
            || job.TimeoutMilliseconds is < 1000 or > 120000 || job.PreviousProcess.ProcessId < 1
            || !job.Listen.IsLoopback || !string.Equals(job.Listen.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || job.Listen.AbsolutePath.Length != 1 || job.Listen.Query.Length != 0 || job.Listen.Fragment.Length != 0 || job.Listen.UserInfo.Length != 0)
        {
            throw new InvalidDataException("Invalid ClientHost update scope.");
        }
        Installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.InstallationRoot));
        if (Path.GetDirectoryName(Installation) is null)
        {
            throw new InvalidDataException("A filesystem root cannot be replaced.");
        }
        Candidate = Installation + ".candidate-" + job.OperationId.ToString("N");
        Backup = Installation + ".backup-" + job.OperationId.ToString("N");
        Operation = OperationDirectory(job.StateDirectory, job.OperationId);
        foreach (string path in new[] { Installation, Candidate, Backup })
        {
            if (UpdateLayout.Contains(path, job.StateDirectory) || UpdateLayout.Contains(job.StateDirectory, path))
            {
                throw new InvalidDataException("ClientHost state must be outside the replaced directories.");
            }
        }
        policy = new(new FileSystemOptions([Installation, Candidate, Backup, Operation]));
        foreach (string root in new[] { Installation, Candidate, Backup, Operation })
        {
            Validate(root);
        }
    }

    /// <summary>既存の自動起動登録が参照し続けるパスです。</summary>
    public string Installation 
    {
        get;
    }

    /// <summary>同一volume上の検証済み候補です。</summary>
    public string Candidate 
    {
        get;
    }

    /// <summary>元リリースを保持する一回専用の退避先です。</summary>
    public string Backup 
    {
        get;
    }

    /// <summary>入替対象外の履歴・helper保存先です。</summary>
    public string Operation 
    {
        get;
    }

    /// <summary>Updaterへ渡す固定入力です。</summary>
    public string JobPath => Path.Combine(Operation, "job.json");

    /// <summary>副作用の直前にも祖先と境界を再確認します。</summary>
    public void Validate(string path) => policy.ValidatePath(path, true);

    /// <summary>同じ操作を二度実行しない永続claimを作ります。</summary>
    public void Claim(string fingerprint)
    {
        string path = Path.Combine(Operation, "claimed");
        Validate(path);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(fingerprint));
        stream.Flush(true);
    }

    /// <summary>実行中のUpdater実体を含めて段階を原子的に保存します。</summary>
    /// <returns>保存した進捗です。</returns>
    public async Task<ClientUpdateProgress> SaveAsync(string state, bool completed, EOutcome outcome, string? reason = null, CancellationToken cancellationToken = default)
    {
        ClientUpdateProgress progress = new(state, completed, outcome, ProcessOs.CurrentIdentity(), DateTimeOffset.UtcNow, reason);
        string path = Path.Combine(Operation, "result.json");
        string temporary = path + ".tmp";
        Validate(path);
        Validate(temporary);
        await UpdateProgressFile.WriteAsync(path, progress, cancellationToken).ConfigureAwait(false);
        return progress;
    }

    /// <summary>既存結果だけを読み、停止したhelperを成功や自動再実行へ置き換えません。</summary>
    /// <returns>保存済みの結果、または未記録です。</returns>
    public static async Task<ClientUpdateProgress?> ReadAsync(string stateDirectory, Guid operationId, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(OperationDirectory(stateDirectory, operationId), "result.json");
        FileAccessPolicy access = new(new FileSystemOptions([stateDirectory]));
        access.ValidatePath(path);
        if (!File.Exists(path))
        {
            return null;
        }
        ClientUpdateProgress result = await UpdateProgressFile.ReadAsync<ClientUpdateProgress>(path, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("ClientHost update result is missing.");
        if (!result.Completed && ProcessOs.IsRunning(result.Worker) != true)
        {
            return result with { State = "Unconfirmed", Completed = true, Outcome = EOutcome.Unknown,
                Reason = "ClientHost updater stopped. Inspect its saved operation; no update was repeated." };
        }
        return result;
    }

    /// <summary>呼出元assembly版と実プロセス識別子を組み合わせます。</summary>
    /// <returns>起動確認用の応答です。</returns>
    public static ClientUpdateHealth Health(string buildVersion) => new(ProcessOs.CurrentIdentity(), buildVersion);

    /// <summary>一回の保存先を固定します。</summary>
    /// <returns>ClientHostの状態領域内の絶対パスです。</returns>
    private static string OperationDirectory(string stateDirectory, Guid operationId) => Path.Combine(stateDirectory, "client-updates", operationId.ToString("N"));
}
