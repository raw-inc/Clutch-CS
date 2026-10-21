using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;

/// <summary>新ClientHostの実体と稼働版を直接確認する応答です。</summary>
/// <param name="Process">応答元のPIDと起動時刻です。</param>
/// <param name="BuildVersion">実行中assemblyの情報版です。</param>
public sealed record ClientUpdateHealth(ProcessIdentity Process, string BuildVersion);
