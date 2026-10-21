using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>表示データの境界、点ごとの寿命、native表示の非干渉性を確認します。</summary>
public sealed class DesktopAnnotationTests
{
    /// <summary>共有領域は別所有者へ引き継がれ、点の時刻は再読取で変わりません。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void SharedAnnotationsKeepPointAgesAndReclaimCursors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string group = "Local\\Contents.Annotation.Test." + Guid.NewGuid().ToString("N");
        using DesktopAnnotationStore first = new(group), second = new(group);
        first.Publish(-100, 120, 2, 1, 10000);
        second.Publish(500, -30, 1, 0, 10000);
        DesktopAnnotationSnapshot initial = second.Read(10000)!;
        Assert.False(initial.Owner);
        Assert.Equal(2, initial.Cursors.Length);
        Assert.NotEqual(initial.Cursors[0][0], initial.Cursors[1][0]);
        first.Publish(-90, 125, 2, 1, 11000);
        DesktopAnnotationSnapshot next = second.Read(11000)!;
        Assert.Equal(10000, next.Points[0][5]);
        Assert.Equal(11000, next.Points[1][5]);
        first.Dispose();
        DesktopAnnotationSnapshot handoff = second.Read(11001)!;
        Assert.True(handoff.Owner);
        Assert.Single(handoff.Cursors);
        Assert.Equal(2, handoff.Points.Length);
        DesktopAnnotationSnapshot faded = second.Read(14001)!;
        Assert.Single(faded.Points);
        Assert.Equal(11000, faded.Points[0][5]);
        Assert.Empty(second.Read(15000)!.Points);
        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(next.Payload(), ContractJson.Options));
        Assert.Equal(1000, payload.RootElement.GetProperty("points")[0][5].GetInt32());
    }

    /// <summary>停止したviewerのcursorとownerは期限で回収し、共有point数は上限を超えません。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void DeadOwnerExpiresAndTheRingRemainsBounded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string group = "Local\\Contents.Annotation.Test." + Guid.NewGuid().ToString("N");
        using DesktopAnnotationStore first = new(group), second = new(group);
        first.Publish(10, 20, 1, 0, 10000);
        Assert.False(second.Read(10000)!.Owner);
        Assert.True(second.Read(12001)!.Owner);
        Assert.Empty(second.Read(12001)!.Cursors);
        for (int index = 0; index < 600; index++)
        {
            second.Publish(index, index, 2, index, 13000);
        }
        DesktopAnnotationSnapshot snapshot = second.Read(13000)!;
        Assert.Equal(512, snapshot.Points.Length);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot.Payload(), ContractJson.Options).Length < 65536);
    }

    /// <summary>新種別は明示的な固定境界を持ち、座標とphaseの不正を拒否します。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void AnnotationWireRejectsMalformedCoordinatesAndFlags()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DesktopAnnotations annotations = new([new(0, "screen", -100, 0, 800, 600, true)]);
        annotations.SetViewOnly(true);
        annotations.Apply(Message(13, 0, 799, 599, 2, 1));
        Assert.Throws<InvalidDataException>(() => annotations.Apply(Message(13, 0, 800, 599, 2, 1)));
        Assert.Throws<InvalidDataException>(() => annotations.Apply(Message(13, 0, 10, 10, 3, 1)));
        Assert.Throws<InvalidDataException>(() => annotations.Apply(Message(13, 0, 10, 10, 0, 0)));
        Assert.Throws<InvalidDataException>(() => annotations.Apply(Message(1, 0, 10, 10, 1, 0)));
        byte[] header = new byte[24];
        DesktopWire.WriteHeader(header, 12, 0, 65536, false, 0);
        Assert.Equal(65536, DesktopWire.PayloadLength(header));
        DesktopWire.WriteHeader(header, 12, 1, 1, false, 0);
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
        DesktopWire.WriteHeader(header, 12, 0, 1, true, 0);
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
    }

    /// <summary>同じstrokeでも古い位置から薄くなり、pixelは常にpremultipliedです。</summary>
    [Fact]
    public void RasterUsesIndividualPointAgeAndPhysicalMonitorCoordinates()
    {
        AnnotationRaster raster = new();
        DesktopMonitor monitor = new(0, "negative", -200, -100, 200, 200, true);
        DesktopAnnotationSnapshot snapshot = new(1, 4000, true, [], [[1, 0xff0000, 1, -180, -50, 1000], [2, 0xff0000, 1, -100, -50, 3000]]);
        Assert.True(raster.Render(monitor, snapshot));
        byte old = raster.Pixels[((-50 - raster.Top) * raster.Width - 180 - raster.Left) * 4 + 3];
        byte recent = raster.Pixels[((-50 - raster.Top) * raster.Width - 100 - raster.Left) * 4 + 3];
        Assert.InRange(old, 30, 45);
        Assert.InRange(recent, 105, 120);
        Assert.True(old < recent);
        for (int index = 0; index < raster.Pixels.Length; index += 4)
        {
            Assert.Equal(raster.Pixels[index + 3], raster.Pixels[index + 2]);
            Assert.Equal(0, raster.Pixels[index]);
        }
        Assert.False(raster.Render(new(1, "other", 0, 500, 100, 100, false), snapshot));
    }

    /// <summary>実windowのstyleとcapture除外を読み、描画してもforegroundとcursorが動かないことを確認します。</summary>
    [Fact, SupportedOSPlatform("windows")]
    public void NativeOverlayIsClickThroughCaptureExcludedAndDoesNotActivate()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }
        IntPtr before = GetForegroundWindow();
        using WindowsAnnotationOverlay overlay = new();
        overlay.Update(new(0, "probe", 0, 0, 100, 100, true), new(1, 10000, true, [[0x44aaff, 10, 10]], []));
        IntPtr window = Assert.Single(DesktopAnnotationProcessTests.Windows(Environment.ProcessId));
        Assert.True(IsWindowVisible(window));
        Assert.Equal(0x080800a8L, GetWindowLongPtrW(window, -20).ToInt64() & 0x080800a8L);
        Assert.True(GetWindowDisplayAffinity(window, out uint affinity));
        Assert.Equal(0x11u, affinity);
        Assert.Equal(before, GetForegroundWindow());
        overlay.Hide();
        Assert.False(IsWindowVisible(window));
    }

    /// <summary>実workerと共通の固定長messageを生成します。</summary>
    /// <returns>network byte orderの6整数です。</returns>
    internal static byte[] Message(params int[] values)
    {
        byte[] result = new byte[24];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(index * 4), values[index]);
        }
        return result;
    }

    /// <summary>現在の入力先を借用します。</summary>
    /// <returns>foregroundです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>OSへ実際に登録されたstyleを読みます。</summary>
    /// <returns>styleです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    /// <summary>実際のcapture制約を読みます。</summary>
    /// <returns>読取成功です。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    /// <summary>windowの表示状態を読みます。</summary>
    /// <returns>表示中かです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
