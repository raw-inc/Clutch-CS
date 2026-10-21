using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;

/// <summary>DXGI_ADAPTER_DESC1の受信ABIです。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsGpuAdapterDescription
{
    /// <summary>OSの接続実装名です。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string Name;

    /// <summary>PCI vendorです。</summary>
    internal uint VendorId;

    /// <summary>PCI deviceです。</summary>
    internal uint DeviceId;

    /// <summary>subsystem識別子です。</summary>
    internal uint SubsystemId;

    /// <summary>hardware revisionです。</summary>
    internal uint Revision;

    /// <summary>専用video memoryです。</summary>
    internal UIntPtr DedicatedVideoMemory;

    /// <summary>専用system memoryです。</summary>
    internal UIntPtr DedicatedSystemMemory;

    /// <summary>共有system memoryです。</summary>
    internal UIntPtr SharedSystemMemory;

    /// <summary>LUID下位部です。</summary>
    internal uint LuidLow;

    /// <summary>LUID上位部です。</summary>
    internal uint LuidHigh;

    /// <summary>software 接続実装等の区別です。</summary>
    internal uint Flags;

    /// <summary>OSで確認した接続実装名を表示します。</summary>
    /// <returns>接続実装名です。</returns>
    public override readonly string ToString() => Name;
}
