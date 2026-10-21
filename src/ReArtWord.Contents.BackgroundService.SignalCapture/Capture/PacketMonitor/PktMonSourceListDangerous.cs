using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>PktMon が返した有効長の中だけでデータソースの構造体を読みます。</summary>
internal static class PktMonSourceListDangerous
{
    /// <summary>名前の重複を許容し、ID と名前で対象を識別します。</summary>
    /// <returns>借用ポインターと管理下へコピーした表示情報。</returns>
    internal static IReadOnlyList<(nint Pointer, CaptureAdapter Adapter, PktMonSourceIdentity Identity)> ReadDangerous(nint buffer, int length)
    {
        if (buffer == 0 || length < 8)
        {
            throw new InvalidOperationException("Invalid Packet Monitor data-source list.");
        }

        int count = Marshal.ReadInt32(buffer);
        if (count < 0 || (length - 8) / nint.Size < count)
        {
            throw new InvalidOperationException("Invalid Packet Monitor data-source count.");
        }

        var result = new List<(nint, CaptureAdapter, PktMonSourceIdentity)>(count);
        int listEnd = 8 + count * nint.Size;
        for (int i = 0; i < count; i++)
        {
            nint source = Marshal.ReadIntPtr(buffer, 8 + i * nint.Size);
            long relative = source - buffer;

            // Name[64] / Description[128] と後続の ID・Detail union を含む ABI は 424 bytes です。
            if (relative < listEnd || length - 424 < relative)
            {
                throw new InvalidOperationException("Packet Monitor data-source reference is outside the valid buffer.");
            }

            string name = (Marshal.PtrToStringUni(source + 4, 64) ?? "").Split('\0')[0];
            string description = (Marshal.PtrToStringUni(source + 132, 128) ?? "").Split('\0')[0];
            uint id = (uint)Marshal.ReadInt32(source, 388);
            uint secondaryId = (uint)Marshal.ReadInt32(source, 392);
            uint parentId = (uint)Marshal.ReadInt32(source, 396);
            Guid interfaceId = Guid.Empty;

            // GUID と MAC の同時フラグでは union の先頭が MAC に上書きされる実装があるため、GUID 専用時だけ照合に使います。
            if (Marshal.ReadInt32(source, 400) == 1)
            {
                interfaceId = Marshal.PtrToStructure<Guid>(source + 408);
            }

            bool isSwitchPort = string.Equals(name, "vmswitch.sys", StringComparison.OrdinalIgnoreCase);
            string identity = $"{id}:{name}";
            string display = DisplayText.Safe($"[{id}] {description} / {name}");
            string details = "Packet Monitor / Ethernet and IP / maximum 9000 bytes\n" + DisplayText.Safe(description);
            if (isSwitchPort)
            {
                details += "\n" + UiText.Get("Virtual NIC: only NIC Rx/Tx observations are retained. Switch and drop observations are excluded. Use Npcap if VLAN isolation is required.");
            }

            result.Add((source, new CaptureAdapter(identity, display, details), new PktMonSourceIdentity(id, secondaryId, parentId, interfaceId, isSwitchPort)));
        }

        return result;
    }
}
