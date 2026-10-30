namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>利用者が明示するグループ操作です。</summary>
public enum EProcessGroupCommand
{
    /// <summary>停止しているメンバーを設定順に起動します。</summary>
    Start,

    /// <summary>全員を停止してから設定順に起動します。</summary>
    Restart,

    /// <summary>全員を停止し、自動復旧を抑止します。</summary>
    Stop,
}
