using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>Windows SDKのMIB_IF_ROW2配置。文字列や未使用の統計値はマーシャリングしません。</summary>
[StructLayout(LayoutKind.Explicit, Size = 1352)]
internal struct WindowsInterfaceRow
{
    /// <summary>インターフェースのローカル識別子です。</summary>
    [FieldOffset(0)] internal ulong Luid;

    /// <summary>再有効化時の実体変更を識別する番号です。</summary>
    [FieldOffset(8)] internal uint Index;

    /// <summary>インターフェースの永続識別子です。</summary>
    [FieldOffset(12)] internal Guid Identity;

    /// <summary>ループバック・トンネルを除外する種別です。</summary>
    [FieldOffset(1128)] internal uint Type;

    /// <summary>bit 0がHardwareInterface、bit 1がFilterInterfaceです。</summary>
    [FieldOffset(1152)] internal byte Flags;

    /// <summary>累積受信バイト数です。</summary>
    [FieldOffset(1208)] internal ulong Received;

    /// <summary>累積送信バイト数です。</summary>
    [FieldOffset(1280)] internal ulong Sent;

    /// <summary>OSが物理インターフェースと確認した転送カウンターを選びます。</summary>
    /// <returns>物理NICの場合だけ累積カウンターです。</returns>
    internal readonly Counters.TransferCounter? Counter()
    {
        if ((Flags & 3) == 1 && Type is not (24 or 131))
        {
            return new(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Identity:D}/{Luid}/{Index}"), Received, Sent);
        }
        return null;
    }
}
