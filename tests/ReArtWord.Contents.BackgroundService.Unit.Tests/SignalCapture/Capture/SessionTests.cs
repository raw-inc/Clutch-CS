using System.Globalization;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.Tests;

/// <summary>取得元数、除外・欠落計数、停止時の所有権を検証します。</summary>
public sealed class SessionTests
{
    /// <summary>再開時に前回の受信・欠落・除外件数と待機パケットを持ち越しません。</summary>
    [Fact]
    public void RestartsWithResetCountersAndAnEmptyQueue()
    {
        var captures = new List<TestCapture>();
        var queue = new PacketQueue(maximumCount: 1);
        using var session = new WorkerSession(new CaptureFactory(true), queue, _ =>
        {
            var capture = new TestCapture();
            captures.Add(capture);
            return capture;
        });
        var command = new PipeCommand("start", new[] { new SourceRequest("demo", "demo0") }, new[] { new EndpointRequest("127.0.0.2", 80) });
        Assert.True(session.Start(command).Running);
        TestCapture first = Assert.Single(captures);
        first.Emit(DemoCapture.Create(0));
        first.Emit(DemoCapture.Create(1));
        first.Emit(new PacketRecord { Data = new byte[9001] });
        first.Emit(DemoCapture.Create(2));
        Assert.Equal(7, session.Statistics().Received);
        Assert.Equal(4, session.Statistics().Dropped);
        Assert.Equal(4, session.Statistics().Suppressed);
        Assert.False(session.Stop().Running);

        Assert.True(session.Start(command).Running);
        Assert.Equal(2, captures.Count);
        Assert.Equal(1, first.Stops);
        Assert.Equal(1, first.Disposals);
        Assert.Null(queue.Read());
        Assert.Equal(0, queue.Dropped);

        // 新しい取得元が報告するネイティブ側の件数だけが残ります。
        Assert.Equal(3, session.Statistics().Received);
        Assert.Equal(2, session.Statistics().Dropped);
        Assert.Equal(3, session.Statistics().Suppressed);
        captures[1].Emit(DemoCapture.Create(0));
        Assert.Equal(4, session.Statistics().Received);
        Assert.NotNull(session.Encode(Assert.IsType<QueuedPacket>(queue.Read())));
    }

    /// <summary>後続 NIC の列挙待ちが先行 NIC の解析と状態通知を止めません。</summary>
    /// <returns>開始中の並行動作の検証。</returns>
    [Fact]
    public async Task EncodesAndReportsWhileAnotherSourceIsOpeningAsync()
    {
        using var opening = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var first = new TestCapture();
        using var second = new TestCapture(beforeEnumeration: () =>
        {
            opening.Set();
            resume.Wait(CancellationToken.None);
        });
        var queue = new PacketQueue();
        int created = 0;
        using var session = new WorkerSession(new CaptureFactory(true), queue, _ =>
        {
            if (Interlocked.Increment(ref created) == 1)
            {
                return first;
            }
            return second;
        });
        Task<PipeEvent> starting = Task.Run(() => session.Start(new PipeCommand("start", new[] { new SourceRequest("demo", "demo0"), new SourceRequest("demo", "demo1") })));
        List<Task> operations = [starting];
        try
        {
            Assert.True(await Task.Run(() => opening.Wait(TimeSpan.FromSeconds(5))));
            first.Emit(DemoCapture.Create(0));
            QueuedPacket packet = Assert.IsType<QueuedPacket>(queue.Read());
            Task<byte[]?> encoding = Task.Run(() => session.Encode(packet));
            operations.Add(encoding);
            byte[]? frame = await encoding.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(frame);
            Task<PipeEvent> readingState = Task.Run(session.State);
            operations.Add(readingState);
            PipeEvent state = await readingState.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.Running);
            Task<PipeEvent> readingStatistics = Task.Run(session.Statistics);
            operations.Add(readingStatistics);
            PipeEvent statistics = await readingStatistics.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(7, statistics.Received);
            Assert.Equal(0, queue.Dropped);
            session.ReplaceExclusions(new PipeCommand("exclusions", ExcludedTcpEndpoints: new[] { new EndpointRequest("127.0.0.2", 80) }));
            first.Emit(DemoCapture.Create(2));
            Assert.Null(queue.Read());
            Assert.False(starting.IsCompleted);
        }
        finally
        {
            resume.Set();
            await Task.WhenAll(operations).ConfigureAwait(true);
        }
    }

    /// <summary>ネイティブの停止完了を待つ間も、停止済み世代を出力へ戻しません。</summary>
    /// <returns>停止中の世代切替の検証。</returns>
    [Fact]
    public async Task RejectsPendingPacketsWhileNativeStopIsWaitingAsync()
    {
        using var stopping = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var capture = new TestCapture(beforeStop: () =>
        {
            stopping.Set();
            resume.Wait(CancellationToken.None);
        });
        var queue = new PacketQueue();
        using var session = new WorkerSession(new CaptureFactory(true), queue, _ => capture);
        session.Start(new PipeCommand("start", new[] { new SourceRequest("demo", "demo0") }));
        capture.Emit(DemoCapture.Create(0));
        QueuedPacket pending = Assert.IsType<QueuedPacket>(queue.Read());
        Task<PipeEvent> ending = Task.Run(session.Stop);
        Task<byte[]?> encoding = Task.FromResult<byte[]?>(null);
        try
        {
            Assert.True(await Task.Run(() => stopping.Wait(TimeSpan.FromSeconds(5))));
            encoding = Task.Run(() => session.Encode(pending));
            Assert.Null(await encoding.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(ending.IsCompleted);
        }
        finally
        {
            resume.Set();
            await Task.WhenAll(ending, encoding).ConfigureAwait(true);
        }
        Assert.Equal(3, session.Statistics().Dropped);
    }

    /// <summary>置換前の待機原本へも新しい除外を適用し、不正な置換では集合を保持します。</summary>
    [Fact]
    public void ReplacesExclusionsWithoutResettingStatistics()
    {
        var queue = new PacketQueue();
        using var capture = new TestCapture();
        using var session = new WorkerSession(new CaptureFactory(true), queue, _ => capture);
        session.Start(new PipeCommand("start", new[] { new SourceRequest("demo", "demo0") }));
        capture.Emit(DemoCapture.Create(0));
        capture.Emit(DemoCapture.Create(1));
        session.ReplaceExclusions(new PipeCommand("exclusions", ExcludedEndpoints: new[] { new TransportEndpointRequest("udp", "127.0.0.2", 9000) }));
        Assert.Null(session.Encode(Assert.IsType<QueuedPacket>(queue.Read())));
        Assert.NotNull(session.Encode(Assert.IsType<QueuedPacket>(queue.Read())));
        Assert.Equal(4, session.Statistics().Suppressed);
        Assert.Throws<ArgumentException>(() => session.ReplaceExclusions(new PipeCommand("exclusions", ExcludedEndpoints: new[] { new TransportEndpointRequest("udp", "localhost", 9000) })));
        capture.Emit(DemoCapture.Create(0));
        Assert.Null(queue.Read());
        Assert.Equal(5, session.Statistics().Suppressed);
        session.ReplaceExclusions(new PipeCommand("exclusions"));
        capture.Emit(DemoCapture.Create(0));
        Assert.NotNull(queue.Read());
        Assert.Equal(7, session.Statistics().Received);
        Assert.Equal(2, session.Statistics().Dropped);
    }

    /// <summary>16取得元を独立所有し、繰り返し stop でも一度だけ解放します。</summary>
    [Fact]
    public void ClosesEverySourceAndSeparatesSuppressionFromDrops()
    {
        var captures = new List<TestCapture>();
        var queue = new PacketQueue();
        using var session = new WorkerSession(new CaptureFactory(true), queue, _ =>
        {
            var capture = new TestCapture();
            captures.Add(capture);
            return capture;
        });
        SourceRequest[] requests = Enumerable.Range(0, 16).Select(index => new SourceRequest("demo", "demo" + index.ToString(CultureInfo.InvariantCulture))).ToArray();
        PipeEvent state = session.Start(new PipeCommand("start", requests, new[] { new EndpointRequest("127.0.0.2", 80) }));
        Assert.True(state.Running);
        foreach (TestCapture capture in captures)
        {
            capture.Emit(DemoCapture.Create(0));
            capture.Emit(DemoCapture.Create(2));
        }

        Assert.Equal(80, session.Statistics().Received);
        Assert.Equal(64, session.Statistics().Suppressed);
        Assert.Equal(32, session.Statistics().Dropped);
        Assert.False(session.Stop().Running);
        session.Stop();
        Assert.Null(queue.Read());
        Assert.Equal(48, session.Statistics().Dropped);
        Assert.All(captures, capture =>
        {
            Assert.Equal(1, capture.Stops);
            Assert.Equal(1, capture.Disposals);
        });
    }

    /// <summary>過剰・重複指定はキャプチャ開始前に拒否します。</summary>
    [Fact]
    public void RejectsInvalidSelectionsBeforeOpeningSources()
    {
        using var session = new WorkerSession(new CaptureFactory(true), new PacketQueue());
        Assert.Throws<ArgumentException>(() => session.Start(new PipeCommand("start", Array.Empty<SourceRequest>())));
        Assert.Throws<ArgumentException>(() => session.Start(new PipeCommand("start", Enumerable.Repeat(new SourceRequest("demo", "demo0"), 17).ToArray())));
        Assert.Throws<ArgumentException>(() => session.Start(new PipeCommand("start", new[] { new SourceRequest("demo", "demo0"), new SourceRequest("demo", "demo0") })));
    }
}
