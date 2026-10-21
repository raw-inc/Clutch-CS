using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>無音・静止中も通知し、通信異常時に接続寿命と通知taskを回収します。</summary>
[Collection("TimingSensitive")]
public sealed class DesktopHeartbeatTests
{
    /// <summary>連続する通知は固定wireで読み取れ、取消後に書込みを残しません。</summary>
    /// <returns>通知二回と所有taskの終了確認です。</returns>
    // 抑制理由: Task.WhenAllで通知と送信待ちの終了を確認してから共有資源を破棄します。
    [Theory, SuppressMessage("Reliability", "CA2025", Justification = "開始した二つのタスクはTask.WhenAllで終了を確認します。")]
    [InlineData(0)]
    [InlineData(2500)]
    public async Task HeartbeatsAreFramedAndCancellationStopsWritesAsync(int sendDelayMilliseconds)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        using SemaphoreSlim send = new(0, 1);
        DesktopFault fault = new();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using TwoHeartbeatStream stream = new(stop.CancelAsync);
        using Task released = ReleaseSendAsync(send, sendDelayMilliseconds, lifetime.Token);
        using Task notifications = DesktopHeartbeat.RunAsync(stream, send, lifetime, fault, stop.Token);
        await Task.WhenAll(released, notifications).ConfigureAwait(true);
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

    /// <summary>送信枠が遅れて空いた場合も、完了した通知数で検証します。</summary>
    /// <returns>試験用の送信待ち解除です。</returns>
    private static async Task ReleaseSendAsync(SemaphoreSlim send, int delayMilliseconds, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        send.Release();
    }

    /// <summary>二回の書込みが完了した時点で通知を停止します。</summary>
    private sealed class TwoHeartbeatStream(Func<Task> stop) : MemoryStream
    {
        private int writes;

        /// <summary>実際の書込み回数で止め、取消タイマーの発火順に依存させません。</summary>
        /// <returns>書込みと、二回目の通知後の取消しです。</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (++writes == 2)
            {
                await stop().ConfigureAwait(false);
            }
        }
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
