namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>選択 NIC の観測点を照合し、仮想 NIC ではスイッチ内部や破棄通知を除外します。</summary>
internal sealed class PktMonSourceFilter
{
    private readonly HashSet<uint> componentIds = new();
    private readonly bool virtualInterface;

    /// <summary>非表示の重複登録も照合しますが、同じ親を持つ別ポートは許可しません。</summary>
    internal PktMonSourceFilter(PktMonSourceIdentity selected, IReadOnlyList<PktMonSourceIdentity> sources)
    {
        List<PktMonSourceIdentity> matches = sources.Where(source => source.Id == selected.Id ||
            selected.InterfaceId != Guid.Empty && source.InterfaceId == selected.InterfaceId).Append(selected).ToList();
        virtualInterface = matches.Any(source => source.IsSwitchPort);
        foreach (PktMonSourceIdentity source in matches)
        {
            if (source.Id is > 0 and <= ushort.MaxValue)
            {
                componentIds.Add(source.Id);
            }

            if (source.SecondaryId is > 0 and <= ushort.MaxValue)
            {
                componentIds.Add(source.SecondaryId);
            }
        }

        // 親スイッチは共有されるため、関連 ID として返っても許可対象にはしません。
        foreach (PktMonSourceIdentity source in matches)
        {
            componentIds.Remove(source.ParentId);
        }

        if (componentIds.Count == 0)
        {
            throw new InvalidOperationException("The selected Packet Monitor adapter has no usable component ID. Select Npcap instead.");
        }
    }

    /// <summary>仮想 NIC は NIC 自身の Rx/Tx に限定し、スイッチ内部と破棄地点の観測を持ち込みません。</summary>
    /// <returns>選択対象のキャプチャとして受け入れるか。</returns>
    internal bool Accepts(ushort componentId, ushort direction, uint dropReason, uint dropLocation)
    {
        return componentIds.Contains(componentId) && (!virtualInterface ||
            direction is 3 or 4 && dropReason == 0 && dropLocation == 0);
    }
}
