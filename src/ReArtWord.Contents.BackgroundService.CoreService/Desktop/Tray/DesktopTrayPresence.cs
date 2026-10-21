using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Tray;

/// <summary>認証済み接続の寿命だけ非表示windowを持ち、同一desktopの接続を一つのtrayへ集約します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopTrayPresence : IAsyncDisposable
{
    private static readonly WindowsTrayNative.WindowProcedure procedure = Dispatch;
    private static readonly ConcurrentDictionary<IntPtr, DesktopTrayPresence> owners = new();
    private static readonly ConcurrentDictionary<string, Lazy<ushort>> classes = new(StringComparer.Ordinal);
    private static readonly uint taskbarCreated = WindowsTrayNative.RegisterWindowMessageW("TaskbarCreated");
    private readonly string windowClass;
    private readonly Guid identity;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task completion;
    private IntPtr window;
    private IntPtr previousOwner;
    private Guid previousIdentity;
    private bool viewOnly;

    // 抑制理由: RunのusingがUI thread上で所有・解放し、このfieldはcallbackからの借用だけです。
    [SuppressMessage("Usage", "CA2213", Justification = "Runのusingで専用thread上の解放を保証し、DisposeAsyncはそのtaskの終了を待ちます。")]
    private WindowsDesktopTrayIcon? icon;
    private int disposed;

    /// <summary>同じ製品の接続だけを列挙するclass名です。</summary>
    internal static string ClassName => "ReArtWord.Contents.RemoteDesktop.Connection.v1";

    /// <summary>Shellへ反映できた接続数です。表示状態以外の権限判定には使いません。</summary>
    internal static string CountProperty => "ReArtWord.Contents.RemoteDesktop.ShownCount";

    /// <summary>Shellへ反映できた操作可能な接続数です。</summary>
    internal static string ControlCountProperty => "ReArtWord.Contents.RemoteDesktop.ShownControlCount";

    /// <summary>入力停止が成立した接続だけが1を公開します。未設定は操作可能として数えます。</summary>
    internal static string ViewOnlyProperty => "ReArtWord.Contents.RemoteDesktop.ViewOnly";

    /// <summary>既存worker内の専用threadを使い、映像・入力threadへwindowを作りません。</summary>
    // 抑制理由: UI専用taskはcompletionで保持しDisposeAsyncで終了を確認します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "専用message loopをcompletionで所有します。")]
    internal DesktopTrayPresence(string? group = null)
    {
        windowClass = group ?? ClassName;
        identity = DesktopTrayIdentity.Create(Environment.ProcessPath ?? throw new InvalidOperationException("The tray executable path is unavailable."), windowClass);
        completion = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>workerの入力停止状態を表示threadへ渡します。</summary>
    internal void SetViewOnly(bool value)
    {
        Volatile.Write(ref viewOnly, value);
        IntPtr current = Volatile.Read(ref window);
        if (current != IntPtr.Zero)
        {
            _ = WindowsTrayNative.PostMessageW(current, 0x8001, UIntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>同じ対話desktopの生存windowだけを取得します。ディスクに接続数を残しません。</summary>
    /// <returns>Z順の接続windowです。</returns>
    internal static IReadOnlyList<IntPtr> ReadWindows(string? group = null)
    {
        List<IntPtr> result = [];
        IntPtr after = IntPtr.Zero;
        while (result.Count < 4096)
        {
            after = WindowsTrayNative.FindWindowExW(IntPtr.Zero, after, group ?? ClassName, null);
            if (after == IntPtr.Zero || result.Contains(after))
            {
                break;
            }
            result.Add(after);
        }
        return result;
    }

    /// <summary>windowの生成・message loop・破棄を同じ専用threadで完結します。</summary>
    private void Run()
    {
        IntPtr created = IntPtr.Zero;
        try
        {
            if (stopping.IsCancellationRequested)
            {
                return;
            }
            _ = classes.GetOrAdd(windowClass, name => new Lazy<ushort>(() => Register(name))).Value;
            created = WindowsTrayNative.CreateWindowExW(0x08000080, windowClass, "Clutch-CS Remote Desktop " + identity.ToString("D"), 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, WindowsTrayNative.GetModuleHandleW(null), IntPtr.Zero);
            if (created == IntPtr.Zero)
            {
                return;
            }
            owners[created] = this;
            Volatile.Write(ref window, created);
            using WindowsDesktopTrayIcon ownedIcon = new(created, identity);
            icon = ownedIcon;
            _ = WindowsTrayNative.ChangeWindowMessageFilterEx(created, taskbarCreated, 1, IntPtr.Zero);
            if (WindowsTrayNative.SetTimer(created, new UIntPtr(1), 500, IntPtr.Zero) == UIntPtr.Zero)
            {
                return;
            }
            Update();
            while (!stopping.IsCancellationRequested && 0 < WindowsTrayNative.GetMessageW(out WindowsTrayNative.Message message, IntPtr.Zero, 0, 0))
            {
                _ = WindowsTrayNative.DispatchMessageW(ref message);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ExternalException)
        {
            // Shell表示の障害で、映像・音声・操作の接続を中断しません。
        }
        finally
        {
            if (created != IntPtr.Zero)
            {
                owners.TryRemove(created, out _);
                _ = WindowsTrayNative.RemovePropW(created, ViewOnlyProperty);
                _ = WindowsTrayNative.RemovePropW(created, CountProperty);
                _ = WindowsTrayNative.RemovePropW(created, ControlCountProperty);
                _ = WindowsTrayNative.DestroyWindow(created);
            }
            Volatile.Write(ref window, IntPtr.Zero);
        }
    }

    /// <summary>先に存在するwindowが表示を維持し、終了した場合だけ残存接続へ引き継ぎます。</summary>
    private void Update()
    {
        _ = WindowsTrayNative.SetPropW(window, ViewOnlyProperty, Volatile.Read(ref viewOnly) ? new IntPtr(1)
                                                                                             : IntPtr.Zero);
        IReadOnlyList<IntPtr> peers = ReadWindows(windowClass);
        if (peers.Count == 0)
        {
            return;
        }

        // 非表示・非activateのwindowはZ順を変えません。新規windowは既存表示を奪わず、
        // 既存表示がない初回だけ最も後ろの接続を選びます。
        IntPtr selected = peers.LastOrDefault(peer => WindowsTrayNative.GetPropW(peer, CountProperty) != IntPtr.Zero);
        if (selected == IntPtr.Zero)
        {
            selected = peers[^1];
        }

        // GUIDは同じ配置のworkerで共有します。追随側が古いownerを削除すると、新ownerの表示まで消えるため、引継ぎ当事者だけが回収します。
        if (selected == window && previousOwner != IntPtr.Zero && previousOwner != selected && !WindowsTrayNative.IsWindow(previousOwner))
        {
            WindowsDesktopTrayIcon.Remove(previousOwner, previousIdentity);
        }
        previousOwner = selected;
        char[] title = new char[80];
        int length = WindowsTrayNative.GetWindowTextUnsafe(selected, title, title.Length);
        previousIdentity = Guid.Empty;
        if (36 <= length)
        {
            _ = Guid.TryParse(title.AsSpan(length - 36, 36), out previousIdentity);
        }
        if (selected != window)
        {
            icon?.Hide();
            return;
        }
        int controlling = peers.Count(peer => WindowsTrayNative.GetPropW(peer, ViewOnlyProperty) != new IntPtr(1));
        icon?.Update(peers.Count, controlling);
    }

    /// <summary>表示のためのmessageだけを扱い、通知や操作メニューは開きません。</summary>
    /// <returns>Win32の処理結果です。</returns>
    // 抑制理由: unmanaged callbackから例外を越境させず、trayの障害を接続本体へ波及させません。
    [SuppressMessage("Design", "CA1031", Justification = "unmanaged window callbackの最終境界です。")]
    private static IntPtr Dispatch(IntPtr window, uint message, UIntPtr word, IntPtr parameter)
    {
        try
        {
            if (owners.TryGetValue(window, out DesktopTrayPresence? owner))
            {
                if (message == taskbarCreated)
                {
                    owner.icon?.Reset();
                    owner.Update();
                    return IntPtr.Zero;
                }
                if (message is 0x0113 or 0x8001)
                {
                    owner.Update();
                    return IntPtr.Zero;
                }
                if (message == 0x0010)
                {
                    WindowsTrayNative.PostQuitMessage(0);
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception)
        {
            // 次のtimerでShellへの反映を再確認します。ポップアップによる障害通知は行いません。
        }
        return WindowsTrayNative.DefWindowProcW(window, message, word, parameter);
    }

    /// <summary>同一プロセス内ではclassを一度だけ登録します。</summary>
    /// <returns>登録atomです。</returns>
    private static ushort Register(string name)
    {
        WindowsTrayNative.WindowClass definition = new()
        {
            Size = (uint)Marshal.SizeOf<WindowsTrayNative.WindowClass>(), Name = name,
            Instance = WindowsTrayNative.GetModuleHandleW(null), Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
        };
        ushort atom = WindowsTrayNative.RegisterClassUnsafe(ref definition);
        if (atom == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return atom;
    }

    /// <summary>描画処理より先に表示を終了し、Shell待機が接続回収を無期限に止めないようにします。</summary>
    /// <returns>専用threadの終了確認です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await stopping.CancelAsync().ConfigureAwait(false);
        IntPtr current = Volatile.Read(ref window);
        if (current != IntPtr.Zero)
        {
            _ = WindowsTrayNative.PostMessageW(current, 0x0010, UIntPtr.Zero, IntPtr.Zero);
        }
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Explorerが応答しなくてもCoreが所有するworkerの終了期限を維持します。
        }
        stopping.Dispose();
    }
}
