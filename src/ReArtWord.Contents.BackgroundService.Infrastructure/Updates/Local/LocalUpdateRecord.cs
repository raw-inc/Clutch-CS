using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

/// <summary>一回の固定入力と最後に確認した更新結果です。</summary>
/// <param name="Fingerprint">異なる入力で同じ操作を再利用しない内容識別です。</param>
/// <param name="Job">この一回へ固定した入力です。</param>
/// <param name="Result">最後に永続化を確認した状態です。</param>
/// <param name="Writer">進行中と中断を区別する書込プロセスです。</param>
public sealed record LocalUpdateRecord(string Fingerprint, LocalUpdateJob Job, UpdatePeerResult Result, ProcessIdentity Writer);
