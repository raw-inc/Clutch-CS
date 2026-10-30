namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>グループ操作との競合を判定する、個別アプリ操作の区分です。</summary>
internal enum EProcessGroupAccess
{
    Control,
    Observe,
    Recover,
    Edit,
    Remove,
}
