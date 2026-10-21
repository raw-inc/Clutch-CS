namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>一台のPCに固定した通信インターフェースとローカルIPv4です。</summary>
/// <param name="HostId">実行PCの安定した識別子です。</param>
/// <param name="InterfaceId">そのPC上のNIC識別子です。</param>
/// <param name="LocalAddress">そのNICに割り当てられたIPv4です。</param>
public sealed record HostNetworkBinding(string HostId, string InterfaceId, string LocalAddress);
