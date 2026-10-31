using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>終了後の期限を過ぎても、パイプに届いている出力を保持します。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessPipeTests
{
    /// <summary>読取りの再開前に期限が切れても、BOMとUnicodeを含む既存の出力を読み切ります。</summary>
    /// <returns>書込み側が開いたままでも、追加の到着を待たず終了する確認です。</returns>
    [WindowsTheory]
    [InlineData("", false)]
    [InlineData("v24.19.0\n", false)]
    [InlineData("日本語🚀", false)]
    [InlineData("日本語🚀", true)]
    public async Task BufferedOutputSurvivesAnExpiredDrainDeadline(string expected, bool bom)
    {
        using AnonymousPipeServerStream writer = new(PipeDirection.Out);
        using SafeFileHandle handle = new(writer.ClientSafePipeHandle.DangerousGetHandle(), ownsHandle: false);
        using CancellationTokenSource deadline = new();
        using WindowsProcessPipe pipe = new(handle, deadline.Token);
        using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        byte[] bytes = Encoding.UTF8.GetBytes(expected);
        if (bom)
        {
            bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        }
        await writer.WriteAsync(bytes).ConfigureAwait(true);
        await deadline.CancelAsync().ConfigureAwait(true);

        Assert.Equal(expected, await reader.ReadToEndAsync().ConfigureAwait(true));
        Assert.True(pipe.DrainDeadlineReached);
        Assert.True(writer.CanWrite);
    }

    /// <summary>期限切れ時のバッファーを読み始めた後に増えた出力は、待機を延ばす対象にしません。</summary>
    /// <returns>読取りを区切った時点の出力だけを返す確認です。</returns>
    [WindowsFact]
    public async Task OutputAddedAfterTheDeadlineSnapshotIsNotDrained()
    {
        using AnonymousPipeServerStream writer = new(PipeDirection.Out);
        using SafeFileHandle handle = new(writer.ClientSafePipeHandle.DangerousGetHandle(), ownsHandle: false);
        using CancellationTokenSource deadline = new();
        using WindowsProcessPipe pipe = new(handle, deadline.Token);
        await writer.WriteAsync("before"u8.ToArray()).ConfigureAwait(true);
        await deadline.CancelAsync().ConfigureAwait(true);
        byte[] first = new byte[3];
        Assert.Equal(3, await pipe.ReadAsync(first).ConfigureAwait(true));
        Assert.Equal("bef", Encoding.UTF8.GetString(first));
        await writer.WriteAsync("after"u8.ToArray()).ConfigureAwait(true);
        byte[] remaining = new byte[16];
        Assert.Equal(3, await pipe.ReadAsync(remaining).ConfigureAwait(true));
        Assert.Equal("ore", Encoding.UTF8.GetString(remaining.AsSpan(0, 3)));
        Assert.Equal(0, await pipe.ReadAsync(remaining).ConfigureAwait(true));
        Assert.True(pipe.DrainDeadlineReached);
    }

    /// <summary>読取り呼出し自体の取消は、終了後の排出期限と区別します。</summary>
    /// <returns>取消で受信済みデータを消費しない確認です。</returns>
    [WindowsFact]
    public async Task CallerCancellationLeavesBufferedBytesUnread()
    {
        using AnonymousPipeServerStream writer = new(PipeDirection.Out);
        using SafeFileHandle handle = new(writer.ClientSafePipeHandle.DangerousGetHandle(), ownsHandle: false);
        using WindowsProcessPipe pipe = new(handle, CancellationToken.None);
        await writer.WriteAsync("kept"u8.ToArray()).ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync().ConfigureAwait(true);
        byte[] bytes = new byte[4];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipe.ReadAsync(bytes.AsMemory(), cancellation.Token).AsTask()).ConfigureAwait(true);
        Assert.Equal(4, await pipe.ReadAsync(bytes).ConfigureAwait(true));
        Assert.Equal("kept", Encoding.UTF8.GetString(bytes));
        Assert.False(pipe.DrainDeadlineReached);
    }
}
