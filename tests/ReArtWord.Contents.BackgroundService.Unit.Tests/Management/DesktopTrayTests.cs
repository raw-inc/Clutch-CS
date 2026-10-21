using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>通知を使わない接続数表示と、実Windowsでの所有権引継ぎを確認します。</summary>
public sealed class DesktopTrayTests
{
    /// <summary>三つ以上の接続で旧ownerが終わっても、追随側が引継ぎ済みのGUIDを削除しません。</summary>
    /// <returns>Shell自身が保持するアイコンの存在確認です。</returns>
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task FollowersDoNotDeleteTheNewOwnersSharedIconAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string group = DesktopTrayPresence.ClassName + ".Handoff." + Guid.NewGuid().ToString("N");
        DesktopTrayPresence first = new(group);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await WaitAsync(() => Shown(group).SequenceEqual([1]));
        IntPtr firstWindow = Assert.Single(DesktopTrayPresence.ReadWindows(group));
        DesktopTrayPresence second = new(group);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await WaitAsync(() => Shown(group).SequenceEqual([2]));
        IntPtr secondWindow = DesktopTrayPresence.ReadWindows(group).Single(window => window != firstWindow);
        Assert.True(KillTimer(secondWindow, new UIntPtr(1)));
        DesktopTrayPresence third = new(group);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable thirdLifetime = third.ConfigureAwait(true);
        await WaitAsync(() => DesktopTrayPresence.ReadWindows(group).Count == 3);
        IntPtr thirdWindow = DesktopTrayPresence.ReadWindows(group).Single(window => window != firstWindow && window != secondWindow);
        Assert.True(KillTimer(thirdWindow, new UIntPtr(1)));
        await WaitAsync(() => Shown(group).SequenceEqual([3]));
        await first.DisposeAsync();
        _ = SendMessageW(secondWindow, 0x8001, UIntPtr.Zero, IntPtr.Zero);
        await WaitAsync(() => Shown(group).SequenceEqual([2]));
        _ = SendMessageW(thirdWindow, 0x8001, UIntPtr.Zero, IntPtr.Zero);
        IconIdentifier icon = new() { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<IconIdentifier>(), Guid = DesktopTrayIdentity.Create(Environment.ProcessPath!, group) };
        Assert.Equal(0, ShellNotifyIconGetRect(ref icon, out _));
    }

    /// <summary>Shellが使うicon識別子の配置です。</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal Guid Guid;

        /// <summary>診断用の識別子です。</summary>
        /// <returns>GUIDです。</returns>
        public override readonly string ToString() => Guid.ToString();
    }

    /// <summary>通知領域の矩形です。</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        /// <summary>診断用の矩形です。</summary>
        /// <returns>四辺です。</returns>
        public override readonly string ToString() => FormattableString.Invariant($"{Left},{Top},{Right},{Bottom}");
    }

    /// <summary>アプリ内の表示済みフラグだけでなく、Shell内のアイコンを参照します。</summary>
    /// <returns>存在する場合はS_OKです。</returns>
    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect", ExactSpelling = true), System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int ShellNotifyIconGetRect(ref IconIdentifier identifier, out Rectangle rectangle);

    /// <summary>試験windowの周期更新を止め、引継ぎ当事者と追随側の順序を固定します。</summary>
    /// <returns>解除成功です。</returns>
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true), System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr window, UIntPtr timer);

    /// <summary>製品windowの更新messageを同じUI threadで同期処理します。</summary>
    /// <returns>window procedureの結果です。</returns>
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true), System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, UIntPtr word, IntPtr parameter);

    /// <summary>再接続で同じGUIDを使い、未署名の別配置へ同じ識別子を流用しません。</summary>
    [Fact]
    public void TrayIdentitySurvivesNewWindowsButSeparatesExecutablePaths()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "core.exe");
        Guid identity = DesktopTrayIdentity.Create(executable, "connections");
        Assert.Equal(identity, DesktopTrayIdentity.Create(executable, "connections"));
        Assert.NotEqual(identity, DesktopTrayIdentity.Create(Path.Combine(AppContext.BaseDirectory, "other", "core.exe"), "connections"));
        Assert.NotEqual(identity, DesktopTrayIdentity.Create(executable, "isolated-test"));
        WindowsTrayIconData first = WindowsTrayIconData.Create(new IntPtr(10), IntPtr.Zero, 1, 0, identity);
        WindowsTrayIconData reconnected = WindowsTrayIconData.Create(new IntPtr(20), IntPtr.Zero, 2, 1, identity);
        Assert.Equal(first.Guid, reconnected.Guid);
        Assert.Equal(0x20u, first.Flags & 0x20);
        Assert.Equal(0u, first.Flags & 0x10);
        Assert.Empty(first.Information);
        Assert.Equal(0u, WindowsTrayIconData.Create(new IntPtr(10), IntPtr.Zero, 0, 0).Flags & 0x20);
    }

    /// <summary>数字が変われば実際の描画が変わり、100以上は小さいicon用に短縮します。</summary>
    [Fact]
    public void BadgeShowsCountWithoutLosingTheExactTooltip()
    {
        Assert.Equal("1", DesktopTrayBadge.Label(1));
        Assert.Equal("28", DesktopTrayBadge.Label(28));
        Assert.Equal("99+", DesktopTrayBadge.Label(100));
        (byte[] firstMask, byte[] firstPixels) = DesktopTrayBadge.Render(1, true);
        (byte[] secondMask, byte[] secondPixels) = DesktopTrayBadge.Render(2, true);
        Assert.Equal(128, firstMask.Length);
        Assert.Equal(4096, firstPixels.Length);
        Assert.Equal(firstMask, secondMask);
        Assert.NotEqual(firstPixels, secondPixels);
        Assert.NotEqual(secondPixels, DesktopTrayBadge.Render(2, false).Colors);
        WindowsTrayIconData data = WindowsTrayIconData.Create(new IntPtr(1), new IntPtr(2), 123, 1);
        Assert.Contains("123", data.Tip, StringComparison.Ordinal);
        Assert.Contains("Control/操作可能: 1", data.Tip, StringComparison.Ordinal);
        Assert.Contains("View/未操作: 122", data.Tip, StringComparison.Ordinal);
        Assert.Equal(0u, data.Flags & 0x10);
        Assert.Empty(data.Information);
        Assert.Empty(data.InformationTitle);
        Assert.Equal(0u, data.InformationFlags);
        Assert.Equal(IntPtr.Zero, data.BalloonIcon);
        WindowsTrayIconData viewing = WindowsTrayIconData.Create(new IntPtr(1), new IntPtr(2), 123, 0);
        Assert.Contains("Control/操作可能: 0", viewing.Tip, StringComparison.Ordinal);
        Assert.Equal(data.Flags, viewing.Flags);
        Assert.Empty(viewing.Information);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopTrayBadge.Render(0, false));
    }

    /// <summary>実Shellに一つだけ表示し、最初の接続終了後も残る接続の数へ更新します。</summary>
    /// <returns>2→1→0の表示とExplorer再作成messageからの復帰です。</returns>
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task WindowsTrayAggregatesAndHandsOffWithoutNotificationsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string group = DesktopTrayPresence.ClassName + ".Test." + Guid.NewGuid().ToString("N");
        DesktopTrayPresence first = new(group), second = new(group);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await WaitAsync(() => Shown(group).SequenceEqual([2]) && Controls(group) == 2);
        first.SetViewOnly(true);
        await WaitAsync(() => Shown(group).SequenceEqual([2]) && Controls(group) == 1);
        second.SetViewOnly(true);
        await WaitAsync(() => Shown(group).SequenceEqual([2]) && Controls(group) == 0);
        IntPtr owner = DesktopTrayPresence.ReadWindows(group).Single(window => WindowsTrayNative.GetPropW(window, DesktopTrayPresence.CountProperty) != IntPtr.Zero);
        uint recreate = WindowsTrayNative.RegisterWindowMessageW("TaskbarCreated");
        Assert.True(WindowsTrayNative.PostMessageW(owner, recreate, UIntPtr.Zero, IntPtr.Zero));
        await WaitAsync(() => Shown(group).SequenceEqual([2]) && Controls(group) == 0);
        await first.DisposeAsync();
        await WaitAsync(() => Shown(group).SequenceEqual([1]) && Controls(group) == 0);
        second.SetViewOnly(false);
        await WaitAsync(() => Shown(group).SequenceEqual([1]) && Controls(group) == 1);
        Assert.Single(DesktopTrayPresence.ReadWindows(group));
        await second.DisposeAsync();
        await WaitAsync(() => DesktopTrayPresence.ReadWindows(group).Count == 0);
    }

    /// <summary>未初期化のまま破棄しても接続数を残しません。</summary>
    /// <returns>生成・終了競合後のwindow解放です。</returns>
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task WindowsImmediateDisconnectLeavesNoPresenceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string group = DesktopTrayPresence.ClassName + ".Test." + Guid.NewGuid().ToString("N");
        DesktopTrayPresence presence = new(group);
        await presence.DisposeAsync();
        await WaitAsync(() => DesktopTrayPresence.ReadWindows(group).Count == 0);
    }

    /// <summary>Shellへ実際に反映済みの数を参照します。</summary>
    /// <returns>表示中のiconごとの接続数です。</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int[] Shown(string group) => DesktopTrayPresence.ReadWindows(group)
        .Select(window => WindowsTrayNative.GetPropW(window, DesktopTrayPresence.CountProperty).ToInt32()).Where(count => 0 < count).ToArray();

    /// <summary>表示所有者がShellへ反映した操作可能数を取得します。</summary>
    /// <returns>表示がなければ-1です。</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int Controls(string group)
    {
        IntPtr owner = DesktopTrayPresence.ReadWindows(group).FirstOrDefault(window => WindowsTrayNative.GetPropW(window, DesktopTrayPresence.CountProperty) != IntPtr.Zero);
        return owner == IntPtr.Zero ? -1
                                    : WindowsTrayNative.GetPropW(owner, DesktopTrayPresence.ControlCountProperty).ToInt32();
    }

    /// <summary>native message loopの反映を有限時間だけ待ちます。</summary>
    /// <returns>観測条件の成立です。</returns>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }
}
