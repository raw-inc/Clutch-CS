using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;

/// <summary>既存の起動パス・利用者・保存領域を維持するClientHostの一回の入替です。</summary>
/// <param name="OperationId">全体更新と同じ識別子です。</param>
/// <param name="Manifest">適用するClientHostです。</param>
/// <param name="InstallationRoot">現在のインストール先です。</param>
/// <param name="StateDirectory">入替対象外のClientHost保存領域です。</param>
/// <param name="Listen">保持するloopback HTTP待受です。</param>
/// <param name="PreviousProcess">自然終了を待つ元ClientHost実体です。</param>
/// <param name="TimeoutMilliseconds">停止・起動確認それぞれの有限上限です。</param>
public sealed record ClientUpdateJob(Guid OperationId, ReleaseManifest Manifest, string InstallationRoot, string StateDirectory,
    Uri Listen, ProcessIdentity PreviousProcess, int TimeoutMilliseconds = 60000);
