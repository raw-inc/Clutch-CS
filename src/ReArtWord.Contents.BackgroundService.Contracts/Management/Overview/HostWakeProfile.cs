using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>PCの停止後も使える、最後に確認したNIC選択です。</summary>
/// <param name="HostId">設定を取得したPCです。</param>
/// <param name="Revision">NICと選択の内容版です。</param>
/// <param name="State">取得した物理NIC判定・選択・アドレスです。</param>
/// <param name="Clusters">確認した参加クラスタです。</param>
/// <param name="UpdatedAt">この内容を取得した時刻です。</param>
public sealed record HostWakeProfile(string HostId, string Revision, DeviceWakeState State, IReadOnlyList<string> Clusters, DateTimeOffset UpdatedAt);
