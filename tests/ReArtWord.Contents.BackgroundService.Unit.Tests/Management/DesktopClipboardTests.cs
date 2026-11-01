using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Clipboard;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>明示購読、OS反映確認、有限なテキストと接続寿命を検証します。</summary>
public sealed class DesktopClipboardTests
{
    /// <summary>対話ユーザーの実クリップボードを共有しない子プロセスで、製品のnative処理を試します。</summary>
    /// <returns>別window station上の実際の読み書き結果です。</returns>
    [WindowsPrivilegeFact(true, "CONTENTS_TEST_PRIVATE_CLIPBOARD")]
    public async Task NativeTextRoundTripsInAPrivateWindowStationAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.GetFullPath("../../../Management/FixtureHost/bin/" + configuration + "/net10.0/ContentsDesktopFixture.exe", AppContext.BaseDirectory);
        System.Diagnostics.ProcessStartInfo start = new(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardError = true };
        start.ArgumentList.Add("clipboard-isolated");
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            Assert.True(process.ExitCode == 0, await errors.ConfigureAwait(true));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                await timeout.CancelAsync().ConfigureAwait(true);
                await ((Task)errors).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
    }

    /// <summary>OFFと閲覧専用ではホストを書き換えず、成功した書換えだけを確認します。</summary>
    /// <returns>通常nullと未供給を混同せず、本文を含まない応答を確認します。</returns>
    [Fact]
    public async Task WritesRequireSubscriptionAndAControllableSessionAsync()
    {
        int writes = 0;
        string actual = "original";
        uint version = 1;
        DesktopClipboard clipboard = new(() => version, () => actual, text =>
        {
            writes++;
            actual = text;
            version++;
            return true;
        });
        using MemoryStream stream = new();
        using SemaphoreSlim send = new(1, 1);
        byte[] value = Encoding.UTF8.GetBytes("日本語\r\n🌐");
        await clipboard.WriteAsync(1, value, stream, send).ConfigureAwait(true);
        Assert.Equal(0, writes);
        Assert.Equal(2, Mode(stream));
        clipboard.Configure(true);
        stream.SetLength(0);
        await clipboard.WriteAsync(2, value, stream, send).ConfigureAwait(true);
        Assert.Equal("日本語\r\n🌐", actual);
        Assert.Equal(1, writes);
        Assert.Equal(1, Mode(stream));
        Assert.Equal(36, stream.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(stream.ToArray().AsSpan(28)));
        Assert.Equal(version, BinaryPrimitives.ReadUInt32BigEndian(stream.ToArray().AsSpan(32)));
        clipboard.SetViewOnly(true);
        clipboard.Configure(true);
        stream.SetLength(0);
        await clipboard.WriteAsync(3, value, stream, send).ConfigureAwait(true);
        Assert.Equal(1, writes);
        Assert.Equal(2, Mode(stream));
    }

    /// <summary>未変更のbusyだけは有限に再試行し、書換え中の失敗は重ねません。</summary>
    /// <returns>誤った成功通知と副作用の再実行を防ぐ結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyRetriesAreBoundedAndWriteFailuresAreNotRetriedAsync(bool partialFailure)
    {
        int attempts = 0;
        DesktopClipboard clipboard = new(() => 1, () => string.Empty, _ =>
        {
            attempts++;
            if (partialFailure)
            {
                throw new Win32Exception(5);
            }
            return false;
        });
        clipboard.Configure(true);
        using MemoryStream stream = new();
        using SemaphoreSlim send = new(1, 1);
        await clipboard.WriteAsync(5, Encoding.UTF8.GetBytes("private text"), stream, send).ConfigureAwait(true);
        int expected = partialFailure ? 1
                                      : 4;
        Assert.Equal(expected, attempts);
        Assert.Equal(2, Mode(stream));
        Assert.DoesNotContain("private text", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>他アプリの遅延レンダリング中でもOFFへ切り替え、遅着した本文を送信しません。</summary>
    /// <returns>同期停止がOSの本文取得待ちに巻き込まれないことの確認です。</returns>
    // 抑制理由: finallyでOS境界の模擬待機を解除し、全taskを回収します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyでpublisherとdisableを回収します。")]
    public async Task DelayedClipboardRenderingDoesNotBlockDisablingAsync()
    {
        uint version = 1;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        DesktopClipboard clipboard = new(() => Volatile.Read(ref version), () =>
        {
            entered.TrySetResult();

            // 保留中のOS応答は、試験のfinallyで解放するまで完了させません。
            release.Wait();
            return "late owner text";
        }, _ => throw new InvalidOperationException("Disabled clipboard must not be written."));
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(1, 1);
        using MemoryStream stream = new();
        clipboard.Configure(true);
        Volatile.Write(ref version, 2);
        Task publisher = clipboard.RunAsync(stream, send, lifetime, new DesktopFault(), lifetime.Token);
        Task? disable = null;
        try
        {
            await entered.Task.WaitAsync(lifetime.Token).ConfigureAwait(true);
            disable = Task.Run(() => clipboard.Configure(false), lifetime.Token);
            await disable.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(true);
            await clipboard.WriteAsync(9, Encoding.UTF8.GetBytes("ignored"), stream, send, lifetime.Token).ConfigureAwait(true);
            Assert.Equal(2, Mode(stream));
        }
        finally
        {
            release.Set();
            await lifetime.CancelAsync().ConfigureAwait(true);
            await publisher.ConfigureAwait(true);
            if (disable is not null)
            {
                await disable.ConfigureAwait(true);
            }
        }
        Assert.DoesNotContain("late owner text", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>初期内容・非テキスト・自己反映を送り返さず、実際の更新だけを送信します。</summary>
    /// <returns>本文取得の回数と、取消後の監視終了です。</returns>
    // 抑制理由: finallyで監視をawaitしてから借用資源を解放します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyでpublisherを回収します。")]
    public async Task OnlyChangedSubscribedTextIsReadAsync()
    {
        uint sequence = 1;
        int reads = 0;
        string? value = "existing";
        DesktopClipboard clipboard = new(() => Volatile.Read(ref sequence), () =>
        {
            Interlocked.Increment(ref reads);
            return value;
        }, _ => true);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(1, 1);
        using RecordingStream stream = new();
        Task publisher = clipboard.RunAsync(stream, send, lifetime, new DesktopFault(), lifetime.Token);
        try
        {
            await Task.Delay(150, lifetime.Token).ConfigureAwait(true);
            Assert.Equal(0, Volatile.Read(ref reads));
            clipboard.Configure(true);
            await Task.Delay(150, lifetime.Token).ConfigureAwait(true);
            Assert.Equal(0, Volatile.Read(ref reads));
            value = null;
            Volatile.Write(ref sequence, 2);
            await Task.Delay(150, lifetime.Token).ConfigureAwait(true);
            Assert.False(stream.Completed.Task.IsCompleted);
            value = "host change";
            Volatile.Write(ref sequence, 3);
            await stream.Completed.Task.WaitAsync(lifetime.Token).ConfigureAwait(true);
            clipboard.Configure(false);
            int before = Volatile.Read(ref reads);
            Volatile.Write(ref sequence, 4);
            await Task.Delay(150, lifetime.Token).ConfigureAwait(true);
            Assert.Equal(before, Volatile.Read(ref reads));
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await publisher.ConfigureAwait(true);
        }
        byte[] frame = stream.ToArray();
        Assert.Equal(0, Mode(stream));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(32)));
        Assert.Equal("host change", DesktopClipboardWire.ReadText(frame.AsSpan(36)));
        Assert.Equal(frame.Length - 24, DesktopWire.PayloadLength(frame.AsSpan(0, 24)));
    }

    /// <summary>空文字と上限値を許可し、不正な長さ・NUL・UTF-8は置換前に拒否します。</summary>
    [Fact]
    public void TextAndFrameBoundariesAreValidated()
    {
        Assert.Equal(string.Empty, DesktopClipboardWire.ReadText([]));
        byte[] maximum = Encoding.UTF8.GetBytes(new string('x', DesktopClipboardWire.MaximumTextBytes));
        Assert.Equal(maximum.Length, DesktopClipboardWire.ReadText(maximum).Length);
        Assert.Throws<InvalidDataException>(() => DesktopClipboardWire.ReadText(new byte[DesktopClipboardWire.MaximumTextBytes + 1]));
        Assert.Throws<InvalidDataException>(() => DesktopClipboardWire.ReadText([65, 0, 66]));
        Assert.Throws<InvalidDataException>(() => DesktopClipboardWire.ReadText([0xff]));
        byte[] input = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(input, 16);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), maximum.Length);
        Assert.Equal(24 + maximum.Length, DesktopClipboardWire.InputLength(input));
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), maximum.Length + 1);
        Assert.Throws<InvalidDataException>(() => DesktopClipboardWire.InputLength(input));
        byte[] frame = DesktopClipboardWire.Frame(0, 0, uint.MaxValue, new string('x', maximum.Length));
        Assert.Equal(12 + maximum.Length, DesktopWire.PayloadLength(frame.AsSpan(0, 24)));
        frame[15] = 1;
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(frame.AsSpan(0, 24)));
    }

    /// <summary>通知の種類を公開されたバイト列から取得します。</summary>
    /// <returns>更新・反映確認・失敗のいずれかです。</returns>
    private static int Mode(MemoryStream stream) => BinaryPrimitives.ReadInt32BigEndian(stream.ToArray().AsSpan(24));

    /// <summary>本文の記録が終わった後だけ試験を再開します。</summary>
    private sealed class RecordingStream : MemoryStream
    {
        internal TaskCompletionSource Completed
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>一フレームの全記録を確認します。</summary>
        /// <returns>記録の完了です。</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Completed.TrySetResult();
        }
    }
}
