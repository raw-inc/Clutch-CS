namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>停止前に確認したNICへ送る一回の起動要求です。</summary>
/// <param name="OperationId">同じ要求の識別子です。</param>
/// <param name="HostId">起動を試みるPCです。</param>
/// <param name="MacAddresses">保存された選択済みNICのMACです。</param>
public sealed record HostWakeRequest(Guid OperationId, string HostId, IReadOnlyList<string> MacAddresses);
