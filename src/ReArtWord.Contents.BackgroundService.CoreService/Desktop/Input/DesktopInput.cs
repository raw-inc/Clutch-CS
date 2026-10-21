using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;

/// <summary>固定入力バッファと押下状態を所有し、終了時に遠隔押下を解放します。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class DesktopInput : IDisposable
{
    private readonly bool[] keys = new bool[384];
    private readonly bool[] buttons = new bool[3];
    private readonly DesktopMonitor[] monitors;
    private readonly IntPtr input;
    private readonly int size;
    private readonly int offset;
    private readonly Action<IntPtr, int> send;
    private readonly int left;
    private readonly int top;
    private readonly long width;
    private readonly long height;
    private bool disposed;

    /// <summary>この接続の操作入力を停止しているかです。旧クライアントは既定の操作可能です。</summary>
    internal bool ViewOnly
    {
        get; private set;
    }

    /// <summary>セッションの画面座標を固定し、Win32 INPUT領域を一度だけ確保します。</summary>
    internal DesktopInput(DesktopMonitor[] monitors, Action<IntPtr, int>? send = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(monitors.Length);
        this.monitors = monitors;
        this.send = send ?? SendNative;
        left = monitors.Min(screen => screen.Left);
        top = monitors.Min(screen => screen.Top);
        width = monitors.Max(screen => (long)screen.Left + screen.Width) - left;
        height = monitors.Max(screen => (long)screen.Top + screen.Height) - top;
        size = 28;
        offset = 4;
        if (Environment.Is64BitProcess)
        {
            size = 40;
            offset = 8;
        }
        input = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, input, size);
    }

    /// <summary>一メッセージを一度だけ適用します。失敗時は暗黙再送しません。</summary>
    internal void Apply(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (message.Length != 24)
        {
            throw new InvalidDataException("Invalid desktop input size.");
        }
        int kind = BinaryPrimitives.ReadInt32BigEndian(message);
        int monitor = BinaryPrimitives.ReadInt32BigEndian(message[4..]);
        int x = BinaryPrimitives.ReadInt32BigEndian(message[8..]);
        int y = BinaryPrimitives.ReadInt32BigEndian(message[12..]);
        int code = BinaryPrimitives.ReadInt32BigEndian(message[16..]);
        int state = BinaryPrimitives.ReadInt32BigEndian(message[20..]);
        if (kind == 11)
        {
            if (monitor is < 0 or > 1 || x != 0 || y != 0 || code != 0 || state != 0)
            {
                throw new InvalidDataException("Invalid desktop input mode.");
            }
            Release();
            ViewOnly = monitor == 1;
            return;
        }
        if (ViewOnly && kind is >= 1 and <= 5 or 10)
        {
            return;
        }
        if (kind == 10)
        {
            if (monitor != 0 || x != 0 || y != 0 || state != 0 || code == 0 || !Rune.IsValid(code))
            {
                throw new InvalidDataException("Invalid desktop text input.");
            }
            Text(new Rune(code));
            return;
        }
        if (kind == 5)
        {
            Release();
            return;
        }
        if (kind == 4 && code is > 0 and < 256 or > 256 and < 384 && state is 0 or 1)
        {
            Key(code, state == 1);
            keys[code] = state == 1;
            return;
        }
        if (monitor < 0 || monitors.Length <= monitor || kind is < 1 or > 3)
        {
            throw new InvalidDataException("Invalid desktop input.");
        }
        DesktopMonitor screen = monitors[monitor];
        if (x < 0 || y < 0 || screen.Width <= x || screen.Height <= y)
        {
            throw new InvalidDataException("Pointer is outside the selected monitor.");
        }

        // 物理pixelの中心を仮想desktopの絶対座標へ変換し、DPIや画面の負座標に依存させません。
        int absoluteX = (int)Math.Clamp((((long)screen.Left + x - left) * 2 + 1) * 65536 / (width * 2), 0, 65535);
        int absoluteY = (int)Math.Clamp((((long)screen.Top + y - top) * 2 + 1) * 65536 / (height * 2), 0, 65535);
        int flags = 0x0001 | 0x8000 | 0x4000;
        int data = 0;
        if (kind == 2)
        {
            if (code is < 0 or > 2 || state is < 0 or > 1)
            {
                throw new InvalidDataException("Invalid mouse button.");
            }
            flags |= ButtonFlag(code, state == 1);
        }
        if (kind == 3)
        {
            flags |= 0x0800;
            data = Math.Clamp(code, -1200, 1200);
        }
        Mouse(flags, data, absoluteX, absoluteY);
        if (kind == 2)
        {
            buttons[code] = state == 1;
        }
    }

    /// <summary>画面から離れた時と切断時に保持したキーだけを解放します。</summary>
    internal void Release()
    {
        for (int index = 1; index < keys.Length; index++)
        {
            if (keys[index])
            {
                Key(index, false);
                keys[index] = false;
            }
        }
        for (int index = 0; index < buttons.Length; index++)
        {
            if (buttons[index])
            {
                Mouse(ButtonFlag(index, false), 0);
                buttons[index] = false;
            }
        }
    }

    /// <summary>セッションのネイティブ領域を一度だけ解放します。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            Release();
        }
        finally
        {
            disposed = true;
            Marshal.FreeHGlobal(input);
        }
    }

    /// <summary>通常キーはVK、記号キーは物理scan codeで入力します。</summary>
    private void Key(int code, bool down)
    {
        Clear();
        Marshal.WriteInt32(input, 1);
        Marshal.WriteInt16(input, offset, (short)code);
        int flags = 0;
        if (256 < code)
        {
            Marshal.WriteInt16(input, offset, 0);
            Marshal.WriteInt16(input, offset + 2, (short)(code - 256));
            flags = 8;
        }
        if (code is >= 0x21 and <= 0x2e or >= 0x5b and <= 0x5d or 0x6f or 0x90 or 0xa3 or 0xa5)
        {
            flags = 1;
        }
        if (!down)
        {
            flags |= 2;
        }
        Marshal.WriteInt32(input, offset + 4, flags);
        send(input, size);
    }

    /// <summary>端末側で変換確定した一文字をUTF-16の押下・解放として送り、ホストのキー配列へ読み替えません。</summary>
    private void Text(Rune value)
    {
        Span<char> units = stackalloc char[2];
        int count = value.EncodeToUtf16(units);
        for (int index = 0; index < count; index++)
        {
            Clear();
            Marshal.WriteInt32(input, 1);
            Marshal.WriteInt16(input, offset + 2, unchecked((short)units[index]));
            Marshal.WriteInt32(input, offset + 4, 4);
            send(input, size);
            Marshal.WriteInt32(input, offset + 4, 6);
            send(input, size);
        }
    }

    /// <summary>ボタンまたはホイールを入力します。</summary>
    private void Mouse(int flags, int data, int x = 0, int y = 0)
    {
        Clear();
        Marshal.WriteInt32(input, offset, x);
        Marshal.WriteInt32(input, offset + 4, y);
        Marshal.WriteInt32(input, offset + 8, data);
        Marshal.WriteInt32(input, offset + 12, flags);
        send(input, size);
    }

    /// <summary>割当てせずに前の入力構造体を初期化します。</summary>
    private void Clear()
    {
        for (int index = 0; index < size; index += 4)
        {
            Marshal.WriteInt32(input, index, 0);
        }
    }

    /// <summary>OSが拒否した入力を成功としません。</summary>
    private static void SendNative(IntPtr input, int size)
    {
        if (!WindowsInputDesktop.TryAttach(out WindowsInputDesktop scope))
        {
            // 切替中・権限外の入力を再送せず、映像と接続の復帰を妨げません。
            return;
        }
        using (scope)
        {
            using DesktopDpiScope dpi = new();
            if (SendInput(1, input, size) != 1)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    /// <summary>ブラウザの左・中・右をWin32のフラグへ変換します。</summary>
    /// <returns>押下または解放のフラグです。</returns>
    private static int ButtonFlag(int button, bool down)
    {
        int flag = button switch { 0 => 2, 1 => 32, 2 => 8, _ => throw new InvalidDataException("Invalid mouse button.") };
        if (!down)
        {
            flag *= 2;
        }
        return flag;
    }

    /// <summary>INPUTを同期でOSへ渡します。</summary>
    /// <returns>Win32 APIの実行結果です。</returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SendInput(uint count, IntPtr inputs, int size);

}
