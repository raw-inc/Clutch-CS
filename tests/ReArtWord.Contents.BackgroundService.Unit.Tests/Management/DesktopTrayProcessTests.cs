using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実際の対話worker二つで、プロセスを跨ぐ集約と異常終了後の引継ぎを確認します。</summary>
public sealed class DesktopTrayProcessTests
{
    /// <summary>起動待ちは数えず、pipe接続後のworkerだけを数えます。</summary>
    /// <returns>二つの実workerの表示、片方の強制終了、最後の通常切断です。</returns>
    [HostedDesktopFact(requireShell: true), SupportedOSPlatform("windows")]
    public async Task WindowsWorkersShareOneTrayAndRecoverFromOwnerExitAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string firstPipe = "contents-tray-test-" + Guid.NewGuid().ToString("N"), secondPipe = "contents-tray-test-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream first = DesktopWorkerProcess.CreatePipe(firstPipe), second = DesktopWorkerProcess.CreatePipe(secondPipe);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using Process one = Start(firstPipe);
        Task firstDrain = Task.CompletedTask;
        Process? two = null;
        Task secondDrain = Task.CompletedTask;
        try
        {
            await first.WaitForConnectionAsync(timeout.Token).ConfigureAwait(true);
            DesktopWorkerProcess.Verify(first, one);
            firstDrain = first.CopyToAsync(Stream.Null, timeout.Token);
            await WaitAsync(() => Displayed([one.Id]), timeout.Token).ConfigureAwait(true);
            two = Start(secondPipe);
            await second.WaitForConnectionAsync(timeout.Token).ConfigureAwait(true);
            DesktopWorkerProcess.Verify(second, two);
            secondDrain = second.CopyToAsync(Stream.Null, timeout.Token);
            await WaitAsync(() => Displayed([one.Id, two.Id]), timeout.Token).ConfigureAwait(true);
            await first.WriteAsync(Mode(true), timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => Viewing(one.Id) && Displayed([one.Id, two.Id]), timeout.Token).ConfigureAwait(true);
            await second.WriteAsync(Mode(true), timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => Viewing(two.Id) && Displayed([one.Id, two.Id]), timeout.Token).ConfigureAwait(true);
            await first.WriteAsync(Mode(false), timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => !Viewing(one.Id) && Displayed([one.Id, two.Id]), timeout.Token).ConfigureAwait(true);
            one.Kill(true);
            await ProcessCompletion.WaitAsync(one, timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => Displayed([two.Id]) && !Present(one.Id), timeout.Token).ConfigureAwait(true);
            second.Close();
            await ProcessCompletion.WaitAsync(two, timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => !Present(one.Id) && !Present(two.Id), timeout.Token).ConfigureAwait(true);
            Assert.Equal(0, two.ExitCode);
        }
        finally
        {
            first.Close();
            second.Close();
            await timeout.CancelAsync().ConfigureAwait(true);
            Stop(one);
            if (two is not null)
            {
                Stop(two);
                two.Dispose();
            }
            try
            {
                await Task.WhenAll(firstDrain, secondDrain).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // 所有pipeの切断による読取り終了です。
            }
        }
    }

    /// <summary>既存のworker起動入口を使い、テスト専用の製品モードは追加しません。</summary>
    /// <returns>この試験が所有する子プロセスです。</returns>
    private static Process Start(string pipe)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.GetFullPath("../../../../../src/ReArtWord.Contents.BackgroundService.CoreService/bin/" + configuration
            + "/net10.0/Clutch-CS.CoreService.exe", AppContext.BaseDirectory);
        ProcessStartInfo start = new(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--desktop-worker");
        start.ArgumentList.Add(pipe);
        start.ArgumentList.Add("unused-encoder-no-video-selected");
        return Process.Start(start) ?? throw new IOException("Worker did not start.");
    }

    /// <summary>実Shellへ表示した一つのiconが合計を持つか確認します。</summary>
    /// <returns>表示の一致です。</returns>
    [SupportedOSPlatform("windows")]
    private static bool Displayed(int[] processes)
    {
        IReadOnlyList<IntPtr> windows = DesktopTrayPresence.ReadWindows();
        int[] owners = windows.Select(Owner).ToArray();
        IntPtr[] shown = windows.Where(window => WindowsTrayNative.GetPropW(window, DesktopTrayPresence.CountProperty) != IntPtr.Zero).ToArray();
        int controlling = windows.Count(window => WindowsTrayNative.GetPropW(window, DesktopTrayPresence.ViewOnlyProperty) != new IntPtr(1));
        return processes.All(owners.Contains) && shown.Length == 1
            && WindowsTrayNative.GetPropW(shown[0], DesktopTrayPresence.CountProperty).ToInt32() == windows.Count
            && WindowsTrayNative.GetPropW(shown[0], DesktopTrayPresence.ControlCountProperty).ToInt32() == controlling;
    }

    /// <summary>入力処理が確定させた接続モードを確認します。</summary>
    /// <returns>このworkerが未操作として公開されたかです。</returns>
    [SupportedOSPlatform("windows")]
    private static bool Viewing(int process) => DesktopTrayPresence.ReadWindows().Any(window => Owner(window) == process
        && WindowsTrayNative.GetPropW(window, DesktopTrayPresence.ViewOnlyProperty) == new IntPtr(1));

    /// <summary>実クライアントと同じモード変更をpipeへ送ります。</summary>
    /// <returns>24byteのモード指定です。</returns>
    private static byte[] Mode(bool viewOnly)
    {
        byte[] message = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(message, 11);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), viewOnly ? 1
                                                                         : 0);
        return message;
    }

    /// <summary>他の利用者の実接続を終了条件へ巻き込みません。</summary>
    /// <returns>この試験のworkerが残っているかです。</returns>
    [SupportedOSPlatform("windows")]
    private static bool Present(int process) => DesktopTrayPresence.ReadWindows().Any(window => Owner(window) == process);

    /// <summary>OSが保持する所有PIDを取得します。</summary>
    /// <returns>windowの所有プロセスです。</returns>
    private static int Owner(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out uint process);
        return checked((int)process);
    }

    /// <summary>表示対象windowを試験で起動したプロセスへ結び付けます。</summary>
    /// <returns>所有threadです。</returns>
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    /// <summary>条件成立または試験の期限まで待ちます。</summary>
    /// <returns>native側の状態更新です。</returns>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        while (!condition())
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>試験で作成したプロセスだけを停止します。</summary>
    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
            Assert.True(process.WaitForExit(5000));
        }
    }
}
