using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>SYSTEMと対話ユーザーのworkerが同一セッションの一時描画だけを共有するOS境界です。</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsAnnotationMemory
{
    /// <summary>ディスクを使用しない共有領域とmutexを同じ限定ACLで生成します。</summary>
    /// <returns>呼出元が所有するmapとmutexです。</returns>
    // 抑制理由: 取得途中のmapを回収し、mutexの元例外を呼出元へ渡します。
    [SuppressMessage("SourceQuality", "RAW330", Justification = "所有権移動が完了しなかった資源を回収します。")]
    internal static (MemoryMappedFile Map, Mutex Gate) Open(string name, int size)
    {
        // Local名前空間は対話セッション単位。IUは通常権限のClientとSYSTEM workerの共存用です。
        string access = "D:P(A;;GA;;;SY)(A;;GA;;;IU)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(access, 1, out IntPtr descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            SecurityAttributes attributes = new() { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            using SafeFileHandle mapping = CreateFileMappingW(new IntPtr(-1), ref attributes, 4, 0, (uint)size, name);
            if (mapping.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using SafeWaitHandle nativeGate = CreateMutexW(ref attributes, false, name + ".lock");
            if (nativeGate.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            MemoryMappedFile map = MemoryMappedFile.OpenExisting(name);
            try
            {
                return (map, Mutex.OpenExisting(name + ".lock"));
            }
            catch
            {
                map.Dispose();
                throw;
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    /// <summary>継承しない共有objectのsecurity descriptorです。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        internal int Inherit;

        /// <summary>ACL本体を診断へ出しません。</summary>
        /// <returns>構造体sizeです。</returns>
        public override readonly string ToString() => Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>ACL文字列をnative形式へ変換します。</summary>
    /// <returns>変換成功です。</returns>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);

    /// <summary>page fileをbackingとする一時共有領域です。</summary>
    /// <returns>所有handleです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFileMappingW(IntPtr file, ref SecurityAttributes attributes, uint protection, uint high, uint low, string name);

    /// <summary>プロセス間の短い排他です。</summary>
    /// <returns>所有handleです。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeWaitHandle CreateMutexW(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool owned, string name);

    /// <summary>変換済みdescriptorを解放します。</summary>
    /// <returns>解放時は0です。</returns>
    [LibraryImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr memory);
}
