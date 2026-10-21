using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes;

/// <summary>明示登録したアプリケーションだけを操作する共通境界です。</summary>
public interface IProcessService
{
    /// <summary>現在適用する共通既定値です。</summary>
    public ProcessOptions Options
    {
        get;
    }

    /// <summary>Peerローカルの登録一覧を読みます。</summary>
    /// <returns>版付きの登録一覧です。</returns>
    public ValueTask<IReadOnlyList<RegisteredProcess>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>新規登録または確認済み版に対する更新を確定します。</summary>
    /// <returns>永続化の確定結果です。</returns>
    public ValueTask<EOutcome> RegisterAsync(ProcessDefinition definition, string? expectedVersion = null, CancellationToken cancellationToken = default);

    /// <summary>登録した管理対象を一回だけ起動します。</summary>
    /// <returns>実起動と保存の確認結果です。</returns>
    public ValueTask<ProcessOperationResult> StartAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>明示停止方針で通常終了と必要時の強制終了を確認します。</summary>
    /// <returns>実終了または人待ち・結果不明です。</returns>
    public ValueTask<ProcessOperationResult> StopAsync(string id, EProcessStopPolicy policy, int gracefulTimeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>実終了を確認してから、同じ登録を一度だけ再起動します。</summary>
    /// <returns>停止・起動の結果です。停止失敗から起動へ進みません。</returns>
    public ValueTask<ProcessOperationResult> RestartAsync(string id, EProcessStopPolicy policy, int gracefulTimeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>PID再利用を除外して実生存状態を観測します。</summary>
    /// <returns>永続した故障回数と現観測です。</returns>
    public ValueTask<ProcessOperationResult> ObserveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>確認済み異常だけを明示復旧制限内で復旧します。</summary>
    /// <returns>実復旧または抑止・上限・判定不能です。</returns>
    public ValueTask<ProcessOperationResult> RecoverAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>現起動回の汎用条件判定を渡します。</summary>
    /// <returns>旧起動回を除外した保存結果です。</returns>
    public ValueTask<ProcessOperationResult> SignalAsync(string id, ProcessSignal signal, CancellationToken cancellationToken = default);

    /// <summary>人が外部停止前後に復旧抑止を明示します。</summary>
    /// <returns>永続した抑止状態です。</returns>
    public ValueTask<ProcessOperationResult> SetRecoverySuppressedAsync(string id, bool suppressed, CancellationToken cancellationToken = default);

    /// <summary>人が故障区切りを確認して有限回数を初期化します。</summary>
    /// <returns>確認した現状態です。</returns>
    public ValueTask<ProcessOperationResult> ConfirmRecoveryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>人が現物確認した起動結果を登録し、未確定起動を解決します。</summary>
    /// <returns>確認した既存プロセス、または明示した未起動状態です。</returns>
    public ValueTask<ProcessOperationResult> ResolveLaunchAsync(string id, ProcessIdentity? confirmedIdentity, CancellationToken cancellationToken = default);
}
