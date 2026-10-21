using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Audio;

/// <summary>専用スレッド内でWASAPI loopbackのCOMとpacket寿命を所有します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLoopbackCapture : IDisposable
{
    private readonly AutoResetEvent ready = new(false);
    private readonly WindowsAudioNative.GetSize getSize;
    private readonly WindowsAudioNative.GetBuffer getBuffer;
    private readonly WindowsAudioNative.ReleaseBuffer releaseBuffer;
    private IntPtr client;
    private IntPtr capture;
    private bool apartment;
    private bool started;
    private bool disposed;
    private bool discontinuity;

    /// <summary>既定再生deviceを48kHz stereo float32へ共有変換して開きます。</summary>
    internal WindowsLoopbackCapture()
    {
        try
        {
            WindowsCom.Check(WindowsCom.CoInitializeEx(IntPtr.Zero, 0));
            apartment = true;
            CreateClient();
            IntPtr format = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.WriteInt16(format, 0, 3);
                Marshal.WriteInt16(format, 2, 2);
                Marshal.WriteInt32(format, 4, 48000);
                Marshal.WriteInt32(format, 8, 48000 * 8);
                Marshal.WriteInt16(format, 12, 8);
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 0);
                WindowsCom.Check(WindowsCom.Method<WindowsAudioNative.Initialize>(client, 3)(client, 0, 0x88060000, 100000, 0, format, IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeHGlobal(format);
            }
            WindowsCom.Check(WindowsCom.Method<WindowsAudioNative.SetEvent>(client, 13)(client, ready.SafeWaitHandle.DangerousGetHandle()));
            Guid captureId = new("c8adbd64-e71e-48a0-a4de-185c395cd317");
            WindowsCom.Check(WindowsCom.Method<WindowsAudioNative.GetService>(client, 14)(client, ref captureId, out capture));
            getSize = WindowsCom.Method<WindowsAudioNative.GetSize>(capture, 5);
            getBuffer = WindowsCom.Method<WindowsAudioNative.GetBuffer>(capture, 3);
            releaseBuffer = WindowsCom.Method<WindowsAudioNative.ReleaseBuffer>(capture, 4);
            WindowsCom.Check(WindowsCom.Method<WindowsAudioNative.Control>(client, 10)(client));
            started = true;
        }
        finally
        {
            if (!started)
            {
                Dispose();
            }
        }
    }

    /// <summary>音声engineが通知するpacket到着eventです。</summary>
    internal WaitHandle Available => ready;

    /// <summary>借用packetを再利用領域へコピーして直ちに返却します。</summary>
    /// <returns>一packetを取得したか。時刻異常のpacketはsample数0で破棄します。</returns>
    internal bool Read(float[] samples, out int count, out long timestamp, out bool reset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        count = 0;
        timestamp = 0;
        reset = false;
        WindowsCom.Check(getSize(capture, out uint available));
        if (available == 0)
        {
            return false;
        }
        WindowsCom.Check(getBuffer(capture, out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc));
        try
        {
            discontinuity |= (flags & 1) != 0;
            if ((flags & 4) != 0)
            {
                discontinuity = true;
                return true;
            }
            count = checked((int)frames * 2);
            if (samples.Length < count)
            {
                throw new InvalidDataException("Audio packet exceeds the bounded capture buffer.");
            }
            timestamp = checked((long)(qpc / 10));
            reset = discontinuity;
            discontinuity = false;
            if ((flags & 2) != 0)
            {
                samples.AsSpan(0, count).Clear();
            }
            else
            {
                Marshal.Copy(data, samples, 0, count);
            }
            return true;
        }
        finally
        {
            WindowsCom.Check(releaseBuffer(capture, frames));
        }
    }

    /// <summary>作成した専用スレッドで全COMとeventを解放します。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (started)
        {
            _ = WindowsCom.Method<WindowsAudioNative.Control>(client, 11)(client);
        }
        if (capture != IntPtr.Zero)
        {
            Marshal.Release(capture);
            capture = IntPtr.Zero;
        }
        if (client != IntPtr.Zero)
        {
            Marshal.Release(client);
            client = IntPtr.Zero;
        }
        ready.Dispose();
        if (apartment)
        {
            WindowsCom.CoUninitialize();
            apartment = false;
        }
    }

    /// <summary>列挙用interfaceを一時借用し、clientだけを保持します。</summary>
    private void CreateClient()
    {
        Guid classId = new("bcde0395-e52f-467c-8e3d-c4579291692e");
        Guid enumeratorId = new("a95664d2-9614-4f35-a746-de8db63617e6");
        WindowsCom.Check(WindowsCom.CoCreateInstance(ref classId, IntPtr.Zero, 23, ref enumeratorId, out IntPtr enumerator));
        try
        {
            WindowsCom.Check(WindowsCom.Method<WindowsAudioNative.DefaultEndpoint>(enumerator, 4)(enumerator, 0, 0, out IntPtr device));
            try
            {
                Guid clientId = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
                WindowsCom.Check(WindowsCom.Method<WindowsAudioNative.Activate>(device, 3)(device, ref clientId, 23, IntPtr.Zero, out client));
            }
            finally
            {
                Marshal.Release(device);
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }
    }
}
