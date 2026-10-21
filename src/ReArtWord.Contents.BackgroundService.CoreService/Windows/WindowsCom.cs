using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Windows;

/// <summary>固定COM interfaceの同期呼出しをRCWの寿命へ移さず行います。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsCom
{
    /// <summary>呼出側が所有するinterfaceの既知slotを取得します。</summary>
    /// <returns>同期呼出し用delegateです。</returns>
    internal static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    /// <summary>HRESULTの失敗をCOM例外として保持します。</summary>
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    /// <summary>専用音声スレッドのCOM apartmentを開始します。</summary>
    /// <returns>HRESULTです。</returns>
    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CoInitializeEx(IntPtr reserved, uint flags);

    /// <summary>同じ専用スレッドでCOM apartmentを閉じます。</summary>
    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void CoUninitialize();

    /// <summary>Windowsの音声device enumeratorを生成します。</summary>
    /// <returns>HRESULTです。</returns>
    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);
}
