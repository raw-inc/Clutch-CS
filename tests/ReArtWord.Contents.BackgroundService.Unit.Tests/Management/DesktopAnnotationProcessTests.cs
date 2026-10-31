using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>独立した実worker間の共有と、通常切断・異常終了後のnative回収を確認します。</summary>
public sealed class DesktopAnnotationProcessTests
{
    /// <summary>購読しない旧receiverへ新種別を送らず、購読した操作側へ閲覧側の指示を中継します。</summary>
    /// <returns>pipe、共有memory、実windowの確認です。</returns>
    [HostedDesktopTheory, SupportedOSPlatform("windows")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsWorkersShareAnnotationsAndRecoverAfterOwnerExitAsync(bool anotherGroupAlreadyActive)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }
        string firstName = "contents-annotations-" + Guid.NewGuid().ToString("N"), secondName = "contents-annotations-" + Guid.NewGuid().ToString("N");
        string group = "Local\\Contents.Annotation.ProcessTest." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream first = DesktopWorkerProcess.CreatePipe(firstName), second = DesktopWorkerProcess.CreatePipe(secondName);
        using NamedPipeServerStream unrelated = DesktopWorkerProcess.CreatePipe(firstName + "-unrelated");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        Process? one = null, two = null, other = null;
        try
        {
            if (anotherGroupAlreadyActive)
            {
                other = Start(firstName + "-unrelated", group + ".other");
                await unrelated.WaitForConnectionAsync(timeout.Token).ConfigureAwait(true);
                DesktopWorkerProcess.Verify(unrelated, other);
                await ReceiveAsync(unrelated, 1, timeout.Token).ConfigureAwait(true);
                await unrelated.WriteAsync(DesktopAnnotationTests.Message(11, 1), timeout.Token).ConfigureAwait(true);
                await unrelated.WriteAsync(DesktopAnnotationTests.Message(12, 1), timeout.Token).ConfigureAwait(true);
                await ReceiveAsync(unrelated, 12, timeout.Token).ConfigureAwait(true);
                await unrelated.WriteAsync(DesktopAnnotationTests.Message(13, 0, 70, 70, 1, 0), timeout.Token).ConfigureAwait(true);
                await UntilAsync(unrelated, value => value.GetProperty("cursors").GetArrayLength() != 0, timeout.Token).ConfigureAwait(true);
                await WaitAsync(() => Visible(other.Id), "The independent annotation group did not become visible.", timeout.Token).ConfigureAwait(true);
            }
            one = Start(firstName, group);
            two = Start(secondName, group);
            await Task.WhenAll(first.WaitForConnectionAsync(timeout.Token), second.WaitForConnectionAsync(timeout.Token)).ConfigureAwait(true);
            DesktopWorkerProcess.Verify(first, one); DesktopWorkerProcess.Verify(second, two);
            JsonElement metadata = await ReceiveAsync(first, 1, timeout.Token).ConfigureAwait(true);
            await ReceiveAsync(second, 1, timeout.Token).ConfigureAwait(true);
            DesktopMonitor monitor = metadata.Deserialize<DesktopMonitor[]>(ContractJson.Options)![0];
            await first.WriteAsync(DesktopAnnotationTests.Message(11, 1), timeout.Token).ConfigureAwait(true);
            await first.WriteAsync(DesktopAnnotationTests.Message(12, 1), timeout.Token).ConfigureAwait(true);
            await ReceiveAsync(first, 12, timeout.Token).ConfigureAwait(true);
            await first.WriteAsync(DesktopAnnotationTests.Message(13, 0, 40, 40, 2, 1), timeout.Token).ConfigureAwait(true);
            JsonElement sent = await UntilAsync(first, value => value.GetProperty("points").GetArrayLength() != 0, timeout.Token).ConfigureAwait(true);
            Assert.Equal(monitor.Left + 40, Assert.Single(sent.GetProperty("points").EnumerateArray())[3].GetInt32());
            Assert.Single(sent.GetProperty("cursors").EnumerateArray());
            await WaitAsync(() => Visible(one.Id), "The first annotation worker did not become visible.", timeout.Token).ConfigureAwait(true);
            if (other is not null)
            {
                JsonElement isolated = await ReceiveAsync(unrelated, 12, timeout.Token).ConfigureAwait(true);
                Assert.Empty(isolated.GetProperty("points").EnumerateArray());
                Assert.Equal(monitor.Left + 70, Assert.Single(isolated.GetProperty("cursors").EnumerateArray())[1].GetInt32());
                Assert.True(Visible(other.Id));
            }

            // 旧receiverの購読前に表示windowを作らず、新frameも配送しません。
            Assert.False(Present(two.Id));
            await second.WriteAsync(DesktopAnnotationTests.Message(8, 1), timeout.Token).ConfigureAwait(true);
            await ReceiveAsync(second, 11, timeout.Token).ConfigureAwait(true);
            await second.WriteAsync(DesktopAnnotationTests.Message(12, 1), timeout.Token).ConfigureAwait(true);
            JsonElement received = await UntilAsync(second, value => value.GetProperty("points").GetArrayLength() != 0, timeout.Token).ConfigureAwait(true);
            Assert.Equal(sent.GetProperty("points")[0][0].GetInt64(), received.GetProperty("points")[0][0].GetInt64());
            Assert.False(Visible(two.Id));

            // 操作可能な接続からの描画は無視し、既存viewerの色だけを維持します。
            await second.WriteAsync(DesktopAnnotationTests.Message(13, 0, 60, 60, 2, 1), timeout.Token).ConfigureAwait(true);
            JsonElement controlled = await ReceiveAsync(second, 12, timeout.Token).ConfigureAwait(true);
            Assert.Single(controlled.GetProperty("cursors").EnumerateArray());
            one.Kill(true);
            await ProcessCompletion.WaitAsync(one, timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => Visible(two.Id) && !Present(one.Id), "The surviving annotation worker did not take over after the owner exited.", timeout.Token).ConfigureAwait(true);
            JsonElement cleared = await UntilAsync(second, value => value.GetProperty("cursors").GetArrayLength() == 0, timeout.Token).ConfigureAwait(true);
            Assert.Empty(cleared.GetProperty("cursors").EnumerateArray());
            await UntilAsync(second, value => value.GetProperty("points").GetArrayLength() == 0, timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => !Visible(two.Id), "The annotation window remained visible after the shared points expired.", timeout.Token).ConfigureAwait(true);
            second.Close();
            await ProcessCompletion.WaitAsync(two, timeout.Token).ConfigureAwait(true);
            Assert.Equal(0, two.ExitCode);
            Assert.False(Present(two.Id));
            if (other is not null)
            {
                Assert.True(Visible(other.Id));
            }
        }
        finally
        {
            first.Close(); second.Close(); unrelated.Close();
            Stop(one); Stop(two); Stop(other);
        }
    }

    /// <summary>fixtureから製品workerを起動し、稼働中のRemoteDesktopとは注釈領域を分離します。</summary>
    /// <returns>試験が所有する子プロセスです。</returns>
    private static Process Start(string pipe, string group)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.GetFullPath("../../../Management/FixtureHost/bin/" + configuration
            + "/net10.0/ContentsDesktopFixture.exe", AppContext.BaseDirectory);
        ProcessStartInfo start = new(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(pipe); start.ArgumentList.Add(group);
        return Process.Start(start) ?? throw new IOException("Worker did not start.");
    }

    /// <summary>新旧種別の境界を実際の固定headerで検証します。</summary>
    /// <returns>対象kindのJSON、cursor確認時は空objectです。</returns>
    private static async Task<JsonElement> ReceiveAsync(Stream pipe, int expected, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[24];
        while (!cancellationToken.IsCancellationRequested)
        {
            await pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            byte[] bytes = new byte[DesktopWire.PayloadLength(header)];
            await pipe.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            int kind = BinaryPrimitives.ReadInt32BigEndian(header);
            if (kind == 8)
            {
                Assert.Fail("Desktop worker fault: " + System.Text.Encoding.UTF8.GetString(bytes));
            }
            if (expected != 12)
            {
                Assert.NotEqual(12, kind);
            }
            if (kind == expected)
            {
                if (kind == 11)
                {
                    return JsonSerializer.SerializeToElement(new { });
                }
                JsonElement result = JsonSerializer.Deserialize<JsonElement>(bytes);
                Assert.False(expected == 12 && result.TryGetProperty("available", out JsonElement available) && available.ValueKind == JsonValueKind.False,
                    "The annotation worker reported that native annotations are unavailable.");
                return result;
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>snapshotの条件が成立するまで古いframeを排出します。</summary>
    /// <returns>条件に合う最新値です。</returns>
    private static async Task<JsonElement> UntilAsync(Stream pipe, Func<JsonElement, bool> predicate, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonElement result = await ReceiveAsync(pipe, 12, cancellationToken).ConfigureAwait(false);
            if (predicate(result))
            {
                return result;
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>試験processの表示だけを数えます。</summary>
    /// <returns>表示中のwindowの有無です。</returns>
    [SupportedOSPlatform("windows")]
    private static bool Visible(int process) => Windows(process).Any(IsWindowVisible);

    /// <summary>試験processの残存windowを検証します。</summary>
    /// <returns>windowの有無です。</returns>
    [SupportedOSPlatform("windows")]
    private static bool Present(int process) => Windows(process).Count != 0;

    /// <summary>OSから所有PIDを読み、他の接続を試験へ巻き込みません。</summary>
    /// <returns>このprocessが所有する表示windowです。</returns>
    [SupportedOSPlatform("windows")]
    internal static List<IntPtr> Windows(int process)
    {
        List<IntPtr> result = [];
        IntPtr window = IntPtr.Zero;
        while ((window = WindowsTrayNative.FindWindowExW(IntPtr.Zero, window, WindowsAnnotationOverlay.ClassName, null)) != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(window, out uint owner);
            if (owner == process)
            {
                result.Add(window);
            }
        }
        return result;
    }

    /// <summary>非同期のnative反映を有限時間待ちます。</summary>
    /// <returns>条件成立です。</returns>
    private static async Task WaitAsync(Func<bool> condition, string failure, CancellationToken cancellationToken = default)
    {
        try
        {
            while (!condition())
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Assert.Fail(failure);
        }
    }

    /// <summary>この試験が起動したprocessだけを回収します。</summary>
    private static void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                Assert.True(process.WaitForExit(5000));
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>OSのwindow所有者を読みます。</summary>
    /// <returns>thread IDです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    /// <summary>非表示windowを集計から除きます。</summary>
    /// <returns>可視状態です。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
