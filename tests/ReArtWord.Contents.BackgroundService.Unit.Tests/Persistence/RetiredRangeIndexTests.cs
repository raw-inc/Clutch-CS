using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>日時ではなく、実際に退役したIDの集合を保存できることを確認します。</summary>
public sealed class RetiredRangeIndexTests
{
    /// <summary>先に大きい番号を退役しても、欠番の遅延要求は拒否しません。</summary>
    [Fact]
    public void OutOfOrderRetirementPreservesEveryUnretiredGap()
    {
        OperationIdSequence source = new();
        Guid[] ids = Enumerable.Range(0, 257).Select(_ => source.Next()).ToArray();
        RetiredRangeIndex index = new();
        foreach (int position in Enumerable.Range(0, ids.Length).Where(value => value % 2 == 0).Reverse())
        {
            index.Add(RetiredIdRange.Operation(ids[position], true));
        }
        index.Validate();
        for (int position = 0; position < ids.Length; position++)
        {
            Assert.Equal(position % 2 == 0, index.ContainsOperation(ids[position]));
        }
        foreach (Guid id in ids)
        {
            index.Add(RetiredIdRange.Operation(id, true));
        }
        index.Validate();
        Assert.Single(Assert.Single(index.Groups).Value);
        Assert.All(ids, id => Assert.True(index.ContainsOperation(id)));
        Assert.False(index.ContainsOperation(source.Next()));
    }

    /// <summary>端の番号を合成しても桁あふれで広い区間を退役済みにしません。</summary>
    [Fact]
    public void CounterBoundsAndDuplicateRangesDoNotInventRetiredIds()
    {
        RetiredIdRange point = RetiredIdRange.Operation(OperationIdentity.Create(), true);
        RetiredRangeIndex index = new();
        index.Add(point with { First = uint.MaxValue, Last = uint.MaxValue });
        index.Add(point with { First = uint.MaxValue - 1, Last = uint.MaxValue });
        index.Add(point with { First = 0, Last = 0 });
        index.Add(point with { First = 0, Last = 0 });
        index.Validate();
        Assert.Equal(2, Assert.Single(index.Groups).Value.Count);
        Assert.True(index.Contains(point with { First = 0, Last = 0 }));
        Assert.False(index.Contains(point with { First = 1, Last = 1 }));
        Assert.True(index.Contains(point with { First = uint.MaxValue, Last = uint.MaxValue }));
    }

    /// <summary>受付キーの用途・接頭辞・表記を保持し、別の受付の退役証拠へ転用しません。</summary>
    [Theory]
    [InlineData("n")]
    [InlineData("D")]
    [InlineData("b")]
    public void KeyRangesKeepTheOriginalScopeAndRepresentation(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Guid id = OperationIdentity.CreateRequest();
        string text = id.ToString(format);
        if (char.IsUpper(format[0]))
        {
            text = text.ToUpperInvariant();
        }
        string key = "owner:" + text;
        RetiredIdRange point = Assert.IsType<RetiredIdRange>(RetiredIdRange.Key("receipts:fixture", key));
        RetiredRangeIndex index = new();
        index.Add(point);
        Assert.Equal(key, point.KeyAt(point.First));
        Assert.True(index.ContainsKey("receipts:fixture", key));
        Assert.False(index.ContainsKey("receipts:other", key));
        Assert.False(index.ContainsKey("receipts:fixture", text));
        Assert.False(index.ContainsOperation(id));
        RetiredRangeIndex restored = JsonSerializer.Deserialize<RetiredRangeIndex>(JsonSerializer.Serialize(index))!;
        restored.Validate();
        Assert.True(restored.ContainsKey("receipts:fixture", key));
    }

    /// <summary>索引キーと区間の用途が食い違う保存記録を読み流しません。</summary>
    [Fact]
    public void MalformedPersistedBucketsAreRejected()
    {
        RetiredIdRange point = RetiredIdRange.Operation(OperationIdentity.Create(), true);
        RetiredRangeIndex index = new() { Groups = new(StringComparer.Ordinal) { ["wrong-bucket"] = [point] } };
        Assert.Throws<InvalidDataException>(index.Validate);
        Assert.Throws<InvalidDataException>(() => (point with { First = 2, Last = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (point with { Prefix = "not-an-identifier" }).Validate());
        Assert.Throws<InvalidDataException>(() => (point with { Scope = "workflows" }).Validate());
    }
}
