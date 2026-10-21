using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes;

/// <summary>アプリケーションの起動と対話セッションへの通常終了要求をOS境界へ委譲します。</summary>
public interface IProcessPlatform
{
    /// <summary>対話起動に必要なユーザーが利用可能か、副作用を起こさず確認します。</summary>
    /// <returns>起動ユーザーを利用できるかです。</returns>
    public bool IsLaunchUserAvailable(ProcessDefinition definition) => true;

    /// <summary>実アプリケーションを起動し、監視側へプロセスハンドルの所有権を移します。</summary>
    /// <returns>呼出元が破棄する起動済みプロセスです。</returns>
    public Process Start(ProcessDefinition definition);

    /// <summary>PIDと起動時刻を照合したWindowsアプリケーションへ通常終了を要求します。</summary>
    /// <returns>終了要求を送信できたかです。実終了は呼出元が別途待機します。</returns>
    public ValueTask<bool> CloseWindowAsync(ProcessIdentity identity, CancellationToken cancellationToken = default);
}
