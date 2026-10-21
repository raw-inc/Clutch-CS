using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>起動成否が不明だったプロセスの現物確認結果です。</summary>
/// <param name="Id">登録対象。</param>
/// <param name="ConfirmedIdentity">確認した実体。確認済み未起動ならnull。</param>
public sealed record ResolveProcessLaunchRequest(string Id, ProcessIdentity? ConfirmedIdentity);
