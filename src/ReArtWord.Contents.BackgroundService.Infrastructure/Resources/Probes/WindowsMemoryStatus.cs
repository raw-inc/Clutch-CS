using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;

/// <summary>Win32 MEMORYSTATUSEXと同じ配置の受信構造です。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsMemoryStatus
{
    /// <summary>構造体のバイト長です。</summary>
    internal uint Length;

    /// <summary>物理メモリ使用率です。</summary>
    internal uint MemoryLoad;

    /// <summary>全物理メモリです。</summary>
    internal ulong TotalPhysical;

    /// <summary>利用可能な物理メモリです。</summary>
    internal ulong AvailablePhysical;

    /// <summary>コミット limitです。</summary>
    internal ulong TotalPageFile;

    /// <summary>利用可能なコミット量です。</summary>
    internal ulong AvailablePageFile;

    /// <summary>仮想アドレス空間です。</summary>
    internal ulong TotalVirtual;

    /// <summary>利用可能な仮想空間です。</summary>
    internal ulong AvailableVirtual;

    /// <summary>Win32が予約している未使用項目です。</summary>
    internal ulong AvailableExtendedVirtual;

    /// <summary>診断表示用に物理メモリ値を表します。</summary>
    /// <returns>物理メモリの利用可能量と総量です。</returns>
    public override readonly string ToString()
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{AvailablePhysical}/{TotalPhysical}");
    }
}
