namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>登録アプリケーションを起動するアカウントと対話セッションです。</summary>
public enum EProcessLaunchSession
{
    /// <summary>Windowsではログインユーザーのデスクトップ、その他OSではCoreの実行環境です。</summary>
    Automatic,

    /// <summary>Core自身のアカウントとセッションを保持します。</summary>
    CoreAccount,
}
