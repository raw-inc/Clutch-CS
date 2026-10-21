using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>loopback試験で実音声engineへ無音だけを渡します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopSilentPlayback : IDisposable
{
    /// <summary>音を鳴らさず、sample clockを持つ再生を開始します。</summary>
    internal DesktopSilentPlayback(string path)
    {
        using (FileStream file = File.Create(path))
        {
            using BinaryWriter writer = new(file, Encoding.ASCII, true);
            writer.Write("RIFF"u8);
            writer.Write(36 + 19200);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(48000);
            writer.Write(192000);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(19200);
            writer.Write(new byte[19200]);
        }
        if (!PlaySoundW(path, IntPtr.Zero, 0x2000b))
        {
            throw new InvalidOperationException("The silent loopback test source did not start.");
        }
    }

    /// <summary>この試験processの無音再生を終了します。</summary>
    public void Dispose() => _ = PlaySoundW(null, IntPtr.Zero, 0);

    /// <summary>既定deviceの実shared audio engineへ無音WAVを流します。</summary>
    /// <returns>開始または停止できたか。</returns>
    [DllImport("winmm.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string? sound, IntPtr module, uint flags);
}
