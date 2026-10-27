namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>アプリケーションごとの明示復旧条件です。</summary>
/// <param name="MaximumAttempts">起動成功までの復旧試行上限。未指定は共通値です。</param>
/// <param name="Unlimited">このアプリケーションの復旧回数を明示的に無制限にします。</param>
/// <param name="DelayMilliseconds">復旧間隔。未指定は共通値です。</param>
/// <param name="RecoverWhenUnhealthy">生存中の確認済み利用不能も復旧開始条件にします。</param>
/// <param name="UnhealthyStopPolicy">生存中利用不能から停止するときの明示方針です。</param>
/// <param name="StopTimeoutMilliseconds">生存中利用不能を復旧するときの通常終了待ち上限です。</param>
/// <param name="RestartOnNormalExit">終了コード0から再起動するかです。</param>
/// <param name="RestartOnAbnormalExit">終了コードが0以外の終了から再起動するかです。</param>
/// <param name="RestartOnUnknownExit">終了コードを取得できなかった終了から再起動するかです。既存登録の復旧方針を維持します。</param>
public sealed record ProcessRecoveryPolicy(int? MaximumAttempts = null, bool Unlimited = false, int? DelayMilliseconds = null, bool RecoverWhenUnhealthy = false,
    EProcessStopPolicy? UnhealthyStopPolicy = null, int StopTimeoutMilliseconds = 30000, bool RestartOnNormalExit = true, bool RestartOnAbnormalExit = true, bool RestartOnUnknownExit = true);
