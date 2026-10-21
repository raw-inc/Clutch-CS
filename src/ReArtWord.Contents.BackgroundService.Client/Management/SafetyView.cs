using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>現在Peerの明示停止状態の取込結果です。</summary>
internal sealed record SafetyView(bool Loaded, IReadOnlyList<ManualSafetyStatus> States);
