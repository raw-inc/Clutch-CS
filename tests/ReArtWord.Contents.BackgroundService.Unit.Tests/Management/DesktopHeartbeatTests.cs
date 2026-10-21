using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>無音・静止中も通知し、通信異常時に接続寿命と通知taskを回収します。</summary>
public sealed class DesktopHeartbeatTests
{
    /// <summary>連続する通知は固定wireで読み取れ、取消後に書込みを残しません。</summary>
    /// <returns>通知二回と所有taskの終了確認です。</returns>
    [Fact]
    public async Task HeartbeatsAreFramedAndCancellationStopsWritesAsync()
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(1, 1);
        using MemoryStream stream = new();
        DesktopFault fault = new();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        stop.CancelAfter(TimeSpan.FromMilliseconds(2300));
        await DesktopHeartbeat.RunAsync(stream, send, lifetime, fault, stop.Token).ConfigureAwait(true);
        Assert.True(lifetime.IsCancellationRequested);
        byte[] bytes = stream.ToArray();
        Assert.Equal(64, bytes.Length);
        for (int index = 0; index < 2; index++)
        {
            ReadOnlySpan<byte> message = bytes.AsSpan(index * 32, 32);
            Assert.Equal(9, BinaryPrimitives.ReadInt32BigEndian(message));
            Assert.Equal(8, DesktopWire.PayloadLength(message[..24]));
            Assert.Equal(index + 1, BinaryPrimitives.ReadInt64BigEndian(message[24..]));
        }
    }

    /// <summary>送信不能を無期限の生存通知へせず、元の障害を保存します。</summary>
    /// <returns>有限時間での接続取消です。</returns>
    [Fact]
    public async Task WriteFailureCancelsConnectionLifetimeAsync()
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(1, 1);
        using FailedStream stream = new();
        DesktopFault fault = new();
        await DesktopHeartbeat.RunAsync(stream, send, lifetime, fault, lifetime.Token).ConfigureAwait(true);
        Assert.True(lifetime.IsCancellationRequested);
        using MemoryStream diagnosis = new();
        await fault.SendAsync(diagnosis).ConfigureAwait(true);
        Assert.Contains("connection", System.Text.Encoding.UTF8.GetString(diagnosis.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>実transportの書込み失敗境界を代替します。</summary>
    private sealed class FailedStream : MemoryStream
    {
        /// <summary>送信処理だけを失敗させます。</summary>
        /// <returns>通信不能の例外です。</returns>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Disconnected"));
    }
}
