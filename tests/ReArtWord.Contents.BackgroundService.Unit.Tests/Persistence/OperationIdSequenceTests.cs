using System.Buffers.Binary;
using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>時刻を持たないIDの発行順、並行発行、発行元の切替を確認します。</summary>
public sealed class OperationIdSequenceTests
{
    /// <summary>一つの発行元では連番だけが進み、日時として解釈されません。</summary>
    [Fact]
    public void NewOperationsHaveAStableOriginAndNoTimestamp()
    {
        OperationIdSequence source = new();
        Guid first = source.Next();
        for (int index = 0; index < 100; index++)
        {
            Guid next = source.Next();
            Assert.Equal(first.ToString("N")[..24], next.ToString("N")[..24]);
            Assert.Equal((uint)(index + 2), Counter(next));
            Assert.False(OperationIdentity.TryGetIssuedAt(next, out _));
            Assert.Equal('8', next.ToString("N")[12]);
        }
    }

    /// <summary>並行発行でも同じ番号を二度渡さず、完了した発行の間に欠番を作りません。</summary>
    [Fact]
    public void ConcurrentIssuanceDoesNotReuseCounters()
    {
        OperationIdSequence source = new();
        ConcurrentBag<Guid> ids = [];
        Parallel.For(0, 10000, _ => ids.Add(source.Next()));
        Assert.Equal(10000, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10000).Select(value => (uint)value), ids.Select(Counter).Order());
    }

    /// <summary>プロセスの再起動に相当する新しい発行元も、桁あふれ後の発行元も旧番号を再使用しません。</summary>
    [Fact]
    public void RestartAndCounterRolloverUseDifferentOrigins()
    {
        Guid generation = Guid.NewGuid();
        OperationIdSequence source = new(generation, uint.MaxValue - 1);
        Guid last = source.Next(), first = source.Next(), next = source.Next();
        Assert.Equal(uint.MaxValue, Counter(last));
        Assert.Equal(1U, Counter(first));
        Assert.Equal(2U, Counter(next));
        Assert.NotEqual(last.ToString("N")[..24], first.ToString("N")[..24]);
        Assert.Equal(first.ToString("N")[..24], next.ToString("N")[..24]);
        Assert.NotEqual(first.ToString("N")[..24], new OperationIdSequence().Next().ToString("N")[..24]);
    }

    /// <summary>要求と保存操作は別の連番を使用します。</summary>
    [Fact]
    public void RequestsAndWritesUseSeparateOrigins()
        => Assert.NotEqual(OperationIdentity.Create().ToString("N")[..24], OperationIdentity.CreateRequest().ToString("N")[..24]);

    /// <summary>公開されたUUIDv8の末尾32ビットを読みます。</summary>
    /// <returns>発行元内の連番です。</returns>
    private static uint Counter(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = id.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
    }
}
