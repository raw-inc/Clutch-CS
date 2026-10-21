using System.Runtime.InteropServices;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Login;

/// <summary>不変の平文stringを作らず、Windowsへ渡すUnicode領域を使用後に消去します。</summary>
internal sealed class LoginPasswordBuffer : IDisposable
{
    private readonly int bytes;

    /// <summary>末尾のnullを含む有限の領域へコピーします。</summary>
    internal LoginPasswordBuffer(char[] value)
    {
        bytes = checked((value.Length + 1) * 2);
        Pointer = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(value, 0, Pointer, value.Length);
        Marshal.WriteInt16(Pointer, value.Length * 2, 0);
        Value = new(checked((ushort)(value.Length * 2)), checked((ushort)bytes), Pointer);
    }

    /// <summary>パスワード文字列を指すネイティブ領域です。</summary>
    internal IntPtr Pointer
    {
        get; private set;
    }

    /// <summary>LSAへ渡す長さ付きの文字列です。</summary>
    internal LsaText Value
    {
        get;
    }

    /// <summary>全バイトを消去してから領域を解放します。</summary>
    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            Marshal.Copy(new byte[bytes], 0, Pointer, bytes);
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    /// <summary>LSA_UNICODE_STRINGのネイティブ配置です。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct LsaText(ushort Length, ushort MaximumLength, IntPtr Buffer);
}
