using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power.Scheduling;

/// <summary>対象Hostが稼働中に確認したNICだけを、停止後の送信へ引き継ぎます。</summary>
internal sealed record HostPowerWakeProfile(string HostId, DeviceWakeState State);
