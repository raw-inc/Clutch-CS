using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Input;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>カーソルの変更通知・通信境界・接続寿命を検証します。</summary>
public sealed class DesktopCursorTests
{
    /// <summary>同じ種類の位置移動では送信せず、画面移動・非表示・通常への復帰を通知します。</summary>
    /// <returns>初期値と変更だけの固定長通知です。</returns>
    // 抑制理由: finallyでpublisherを取消・awaitしてから借用資源を解放します。
    [Fact, SuppressMessage("Reliability", "CA2025", Justification = "finallyでpublisherをawaitします。")]
    public async Task OnlyShapeAndMonitorChangesArePublishedAsync()
    {
        (int Monitor, EDesktopCursorShape Shape)[] samples =
        [
            (0, EDesktopCursorShape.Default), (0, EDesktopCursorShape.Default),
            (0, EDesktopCursorShape.Text), (0, EDesktopCursorShape.Text),
            (1, EDesktopCursorShape.Text), (1, EDesktopCursorShape.Hidden), (1, EDesktopCursorShape.Default),
        ];
        int read = 0;
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(1, 1);
        using RecordingStream stream = new(5);
        Task publisher = DesktopCursor.RunAsync(() => samples[Math.Min(read++, samples.Length - 1)], stream, send, lifetime, new DesktopFault(), lifetime.Token);
        try
        {
            await stream.Completed.Task.WaitAsync(lifetime.Token).ConfigureAwait(true);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await publisher.ConfigureAwait(true);
        }
        byte[] bytes = stream.ToArray();
        Assert.Equal(5 * 28, bytes.Length);
        int[] monitors = [0, 0, 1, 1, 1];
        EDesktopCursorShape[] shapes = [EDesktopCursorShape.Default, EDesktopCursorShape.Text, EDesktopCursorShape.Text, EDesktopCursorShape.Hidden, EDesktopCursorShape.Default];
        for (int index = 0; index < shapes.Length; index++)
        {
            ReadOnlySpan<byte> message = bytes.AsSpan(index * 28, 28);
            Assert.Equal(11, BinaryPrimitives.ReadInt32BigEndian(message));
            Assert.Equal(monitors[index], BinaryPrimitives.ReadInt32BigEndian(message[4..]));
            Assert.Equal(4, DesktopWire.PayloadLength(message[..24]));
            Assert.Equal((int)shapes[index], BinaryPrimitives.ReadInt32BigEndian(message[24..]));
        }
        Assert.Equal(1, send.CurrentCount);
    }

    /// <summary>カーソル送信失敗は接続を取消し、保持したタスクを終了させます。</summary>
    /// <returns>元の通信障害が記録されたことの確認です。</returns>
    [Fact]
    public async Task CursorWriteFailureClosesTheConnectionAsync()
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(1, 1);
        using FailedStream stream = new();
        DesktopFault fault = new();
        await DesktopCursor.RunAsync(() => (0, EDesktopCursorShape.Wait), stream, send, lifetime, fault, lifetime.Token).ConfigureAwait(true);
        Assert.True(lifetime.IsCancellationRequested);
        Assert.Equal(1, send.CurrentCount);
        using MemoryStream diagnosis = new();
        await fault.SendAsync(diagnosis).ConfigureAwait(true);
        Assert.Contains("cursor", System.Text.Encoding.UTF8.GetString(diagnosis.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>固定長以外・映像用flag・存在しない画面を中継前に拒否します。</summary>
    [Theory, InlineData(3, 0, false), InlineData(5, 0, false), InlineData(4, 0, true), InlineData(4, 32, false)]
    public void InvalidCursorHeadersAreRejected(int length, int monitor, bool flag)
    {
        byte[] header = new byte[24];
        DesktopWire.WriteHeader(header, 11, monitor, length, flag, 0);
        Assert.Throws<InvalidDataException>(() => DesktopWire.PayloadLength(header));
    }

    /// <summary>必要な通知数の書込み完了を時間待ちなしで知らせます。</summary>
    private sealed class RecordingStream(int expected) : MemoryStream
    {
        private int writes;

        /// <summary>試験が待つ実書込み完了です。</summary>
        internal TaskCompletionSource Completed
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>内容を記録してから完了を通知します。</summary>
        /// <returns>記録の完了です。</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (++writes == expected)
            {
                Completed.SetResult();
            }
        }
    }

    /// <summary>送信時の切断を再現します。</summary>
    private sealed class FailedStream : MemoryStream
    {
        /// <summary>最初の書込みを失敗させます。</summary>
        /// <returns>通信不能です。</returns>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Disconnected"));
    }
}
