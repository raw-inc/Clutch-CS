using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>独立helperの進行段階を、操作を再実行せず画面から読み取れるよう保存します。</summary>
public sealed class SetupProgressJournal
{
    private readonly string path;
    private readonly string[] stages;

    /// <summary>実行する操作の工程順と保存先を固定します。</summary>
    public SetupProgressJournal(ESetupAction action, string directory, bool manageWebClient = false)
    {
        path = Path.Combine(directory, "progress.json");
        stages = action switch
        {
            ESetupAction.Install when manageWebClient => ["validate", "verify-package", "copy", "web-client", "configure", "recovery", "register", "start", "ready"],
            ESetupAction.Install => ["validate", "verify-package", "copy", "configure", "recovery", "register", "start", "ready"],
            ESetupAction.Reinstall => ["validate", "verify-package", "stop", "backup", "copy", "recovery", "register", "start", "ready"],
            _ => ["validate", "stop", "unregister", "remove"],
        };
        Current = new(stages[0], TotalSteps: stages.Length);
    }

    /// <summary>クライアント単独の導入工程を使用します。</summary>
    private SetupProgressJournal(string directory)
    {
        path = Path.Combine(directory, "progress.json");
        stages = ["validate", "web-client"];
        Current = new(stages[0], TotalSteps: stages.Length);
    }

    /// <summary>Coreの工程を含めず、クライアントの進捗を保存します。</summary>
    /// <returns>クライアント用の進捗記録です。</returns>
    public static SetupProgressJournal ForWebClient(string directory) => new(directory);

    /// <summary>最後に開始した工程です。失敗時はこの位置を保持します。</summary>
    public OperationProgress Current
    {
        get; private set;
    }

    /// <summary>次の工程開始直前に、それ以前の工程の終了を記録します。</summary>
    /// <returns>進捗の保存完了です。</returns>
    public Task BeginAsync(string stage, CancellationToken cancellationToken = default)
    {
        int index = Array.IndexOf(stages, stage);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Current = new(stage, index, stages.Length);
        return SaveAsync(cancellationToken);
    }

    /// <summary>最終成功を確認した場合だけ全工程を完了にします。</summary>
    /// <returns>進捗の保存完了です。</returns>
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        Current = Current with { CompletedSteps = stages.Length };
        return SaveAsync(cancellationToken);
    }

    /// <summary>原子的置換を妨げず、直近の進捗を読み取ります。</summary>
    /// <returns>開始前または取得できない場合はnullです。</returns>
    public static async Task<OperationProgress?> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        try
        {
            using FileStream stream = new(Path.Combine(directory, "progress.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<OperationProgress>(stream, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>進捗表示の保存失敗だけで実配置を途中中断しません。</summary>
    /// <returns>保存試行の完了です。</returns>
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InstallationFiles.WriteAsync(path, Current, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Setup progress could not be saved: {0}", exception.Message);
        }
    }
}
