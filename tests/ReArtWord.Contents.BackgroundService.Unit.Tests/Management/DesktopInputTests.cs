using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実際のポインターを動かさず、OSへ渡す入力と資源寿命を検証します。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DesktopInputTests
{
    /// <summary>長押し中の各押下をOSへ渡し、離脱時の解放は一回だけにします。</summary>
    [WindowsTheory]
    [InlineData(65)]
    [InlineData(8)]
    [InlineData(37)]
    [InlineData(0x10c)]
    public void RepeatedKeysReachNativeInputAndReleaseOnce(int code)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        List<int> flags = [];
        int offset = Environment.Is64BitProcess ? 8
                                                : 4;
        using DesktopInput input = new(Monitors(), (pointer, _) => flags.Add(Marshal.ReadInt32(pointer, offset + 4)));
        for (int count = 0; count < 5; count++)
        {
            input.Apply(Message(4, 0, 0, 0, code, 1));
        }
        Assert.Equal(5, flags.Count);
        Assert.All(flags, value => Assert.Equal(0, value & 2));
        input.Release();
        input.Release();
        Assert.Equal(6, flags.Count);
        Assert.Equal(2, flags[^1] & 2);
    }

    /// <summary>入力停止は押下を解放し、再開までOSへのすべての操作入力を抑止します。</summary>
    [WindowsFact]
    public void ViewOnlyReleasesHeldInputAndDropsCommandsUntilResumed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        int calls = 0;
        using DesktopInput input = new(Monitors(), (_, _) => calls++);
        Assert.False(input.ViewOnly);
        input.Apply(Message(4, 0, 0, 0, 65, 1));
        input.Apply(Message(2, 0, 10, 10, 0, 1));
        Assert.Equal(2, calls);
        input.Apply(Message(11, 1));
        Assert.True(input.ViewOnly);
        Assert.Equal(4, calls);
        foreach (int kind in new[] { 1, 2, 3, 4, 5, 10 })
        {
            input.Apply(Message(kind, 0, 0, 0, 65, 1));
        }
        input.Apply(Message(11, 1));
        Assert.Equal(4, calls);
        foreach (byte[] invalid in new[] { Message(11, -1), Message(11, 2), Message(11, 0, 1), Message(11, 0, 0, 1), Message(11, 0, 0, 0, 1), Message(11, 0, 0, 0, 0, 1) })
        {
            Assert.Throws<InvalidDataException>(() => input.Apply(invalid));
            Assert.True(input.ViewOnly);
        }
        input.Apply(Message(11, 0));
        Assert.False(input.ViewOnly);
        Assert.Equal(4, calls);
        input.Apply(Message(4, 0, 0, 0, 65, 1));
        Assert.Equal(5, calls);
        input.Release();
        Assert.Equal(6, calls);
    }

    /// <summary>確定したUnicode文字は配列依存のVKを使用せず、サロゲート対も順序を保って一度だけ送ります。</summary>
    [WindowsTheory]
    [InlineData(0x41, "A")]
    [InlineData(0x65e5, "日")]
    [InlineData(0x1f680, "🚀")]
    public void CommittedUnicodeHasBalancedNativeEvents(int code, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        List<byte[]> sent = [];
        using DesktopInput input = new(Monitors(), (pointer, size) =>
        {
            byte[] bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            sent.Add(bytes);
        });
        input.Apply(Message(10, 0, 0, 0, code, 0));
        input.Release();
        Assert.Equal(expected.Length * 2, sent.Count);
        int offset = Environment.Is64BitProcess ? 8
                                                : 4;
        for (int index = 0; index < sent.Count; index++)
        {
            Assert.Equal(1, BitConverter.ToInt32(sent[index]));
            Assert.Equal(0, BitConverter.ToUInt16(sent[index], offset));
            Assert.Equal(expected[index / 2], (char)BitConverter.ToUInt16(sent[index], offset + 2));
            int flags = 4;
            if (index % 2 != 0)
            {
                flags = 6;
            }
            Assert.Equal(flags, BitConverter.ToInt32(sent[index], offset + 4));
        }
    }

    /// <summary>不正なUnicode・余分な入力情報をOSへ渡しません。</summary>
    [WindowsFact]
    public void InvalidTextDoesNotSendOrLeaveHeldInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        int calls = 0;
        using DesktopInput input = new(Monitors(), (_, _) => calls++);
        foreach (int code in new[] { -1, 0, 0xd800, 0xdfff, 0x110000 })
        {
            Assert.Throws<InvalidDataException>(() => input.Apply(Message(10, 0, 0, 0, code, 0)));
        }
        Assert.Throws<InvalidDataException>(() => input.Apply(Message(10, 1, 0, 0, 65, 0)));
        Assert.Throws<InvalidDataException>(() => input.Apply(Message(10, 0, 0, 0, 65, 1)));
        Assert.Throws<InvalidDataException>(() => input.Apply(new byte[20]));
        input.Release();
        Assert.Equal(0, calls);
    }

    /// <summary>記号キーの押下と解放はVKへ読み替えず、同じ物理scan codeで送ります。</summary>
    [WindowsTheory]
    [InlineData(0x0c)]
    [InlineData(0x0d)]
    [InlineData(0x27)]
    [InlineData(0x28)]
    [InlineData(0x1a)]
    [InlineData(0x1b)]
    [InlineData(0x2b)]
    [InlineData(0x73)]
    [InlineData(0x7d)]
    public void PunctuationScanCodesSurviveReleaseWithoutBecomingVolumeOrModifiers(int scan)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        List<byte[]> sent = [];
        using DesktopInput input = new(Monitors(), (pointer, size) =>
        {
            byte[] bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            sent.Add(bytes);
        });
        int offset = Environment.Is64BitProcess ? 8
                                                : 4;
        input.Apply(Message(4, 0, 0, 0, 0x100 | scan, 1));
        input.Release();
        input.Release();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, bytes =>
        {
            Assert.Equal(1, BitConverter.ToInt32(bytes));
            Assert.Equal(0, BitConverter.ToInt16(bytes, offset));
            Assert.Equal(scan, BitConverter.ToInt16(bytes, offset + 2));
        });
        Assert.Equal(8, BitConverter.ToInt32(sent[0], offset + 4));
        Assert.Equal(10, BitConverter.ToInt32(sent[1], offset + 4));
        Assert.Throws<InvalidDataException>(() => input.Apply(Message(4, 0, 0, 0, 256, 1)));
        Assert.Throws<InvalidDataException>(() => input.Apply(Message(4, 0, 0, 0, 384, 1)));
    }

    /// <summary>現在のdesktopへ接続しても、実SendInputに必要な権限を失いません。</summary>
    [WindowsFact]
    public void AttachedDesktopPermitsNativeInputWithoutMovingThePointer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using DesktopInput input = new(Monitors());

        // 未押下のVK_NONAMEを解放するだけで、文字入力・ポインター移動は行いません。
        input.Apply(Message(4, 0, 0, 0, 0xfc, 0));
    }

    /// <summary>左上にある副画面も全画面の物理座標へ対応し、移動と押下を一度で送ります。</summary>
    [WindowsTheory]
    [InlineData(0, 469, 258, 469, 258)]
    [InlineData(1, 0, 0, -1920, -1080)]
    [InlineData(1, 1919, 1079, -1, -1)]
    [InlineData(0, 3839, 2159, 3839, 2159)]
    public void PointerUsesVirtualDesktopAndSingleCombinedInput(int monitor, int x, int y, int expectedX, int expectedY)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        List<byte[]> sent = [];
        using DesktopInput input = new(Monitors(), (pointer, size) =>
        {
            byte[] bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            sent.Add(bytes);
        });
        input.Apply(Message(2, monitor, x, y, 0, 1));
        Assert.Single(sent);
        int offset = 4;
        if (Environment.Is64BitProcess)
        {
            offset = 8;
        }
        Assert.Equal(0, BitConverter.ToInt32(sent[0]));
        Assert.Equal(expectedX, (long)BitConverter.ToInt32(sent[0], offset) * 5760 / 65536 - 1920);
        Assert.Equal(expectedY, (long)BitConverter.ToInt32(sent[0], offset + 4) * 3240 / 65536 - 1080);
        Assert.Equal(0xc003, BitConverter.ToInt32(sent[0], offset + 12));
        input.Release();
        Assert.Equal(2, sent.Count);
        Assert.Equal(4, BitConverter.ToInt32(sent[1], offset + 12));
        input.Release();
        Assert.Equal(2, sent.Count);
    }

    /// <summary>失敗した押下や不正なボタンは保持・再送せず、OS呼出前に拒否します。</summary>
    [WindowsFact]
    public void InvalidOrRejectedInputDoesNotCreateAReleaseOrRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        int calls = 0;
        using DesktopInput input = new(Monitors(), (_, _) =>
        {
            calls++;
            throw new Win32Exception(5);
        });
        Assert.Throws<InvalidDataException>(() => input.Apply(Message(2, 0, 1, 1, 9, 1)));
        Assert.Throws<InvalidDataException>(() => input.Apply(Message(1, 0, 3840, 1, 0, 0)));
        Assert.Equal(0, calls);
        Assert.Throws<Win32Exception>(() => input.Apply(Message(2, 0, 1, 1, 0, 1)));
        input.Release();
        Assert.Equal(1, calls);
    }

    /// <summary>定常のpointer変換とINPUT再利用でmanaged allocationを増やしません。</summary>
    [WindowsFact]
    public void RepeatedPointerInputDoesNotAllocate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using DesktopInput input = new(Monitors(), static (_, _) =>
        {
        });
        byte[] message = Message(1, 0, 469, 258, 0, 0);
        for (int index = 0; index < 1000; index++)
        {
            input.Apply(message);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            input.Apply(message);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>異なる解像度、負の原点を持つ画面構成です。</summary>
    /// <returns>接続開始時の固定画面です。</returns>
    private static DesktopMonitor[] Monitors() => [new(0, "main", 0, 0, 3840, 2160, true), new(1, "left", -1920, -1080, 1920, 1080, false)];

    /// <summary>中継protocolと同じ24byte入力を組み立てます。</summary>
    /// <returns>一つの完全な入力です。</returns>
    private static byte[] Message(params int[] values)
    {
        byte[] bytes = new byte[24];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(index * 4), values[index]);
        }
        return bytes;
    }
}
