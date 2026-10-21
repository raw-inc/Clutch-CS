using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>副作用後の返信不能を未実行と誤分類しないことを確認します。</summary>
public sealed class ManagementRegistryTests
{
    /// <summary>handler完了後の深いJSON変換失敗はUnknownです。</summary>
    /// <returns>一度実行した事実を維持する確認です。</returns>
    [Fact]
    public async Task SerializationFailureAfterMutationIsUnknown()
    {
        ManagementRegistry registry = new();
        int applied = 0;
        object nested = 42;
        int excessiveDepth = 80;
        for (int depth = 0; depth < excessiveDepth; depth++)
        {
            nested = new[] { nested };
        }
        registry.Write("write", (_, _) =>
        {
            applied++;
            return Task.FromResult<object?>(nested);
        });
        ManagementReply result = await registry.ExecuteAsync(Command("write")).ConfigureAwait(true);
        Assert.Equal(1, applied);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("Management.ResponseSerializationFailed", result.Reason);
    }

    /// <summary>容量超過を小さい明示結果へ置換し、元handlerを実行済みとして扱います。</summary>
    /// <returns>巨大返信を未実行へ読み替えない確認です。</returns>
    [Fact]
    public async Task OversizedMutationResponseUsesSmallUnknownResult()
    {
        ManagementRegistry registry = new();
        registry.Write("large", (_, _) => Task.FromResult<object?>(new string('x', 3 * 1024 * 1024)));
        ManagementReply result = await registry.ExecuteAsync(Command("large")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("Management.ResponseTooLarge", result.Reason);
        Assert.Null(result.Value);
    }

    /// <summary>Bodyで拒否した入力はhandlerの副作用前なので未実行です。</summary>
    /// <returns>入力解釈失敗と実行後失敗の区別です。</returns>
    [Fact]
    public async Task InvalidInputIsNotExecutedBeforeMutation()
    {
        ManagementRegistry registry = new();
        int applied = 0;
        registry.Write("input", (body, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            _ = ManagementRegistry.Body<ManualSafetyChange>(body);
            applied++;
            return Task.FromResult<object?>(true);
        });
        ManagementReply result = await registry.ExecuteAsync(Command("input") with { Payload = NodeValue.FromObject(42) }).ConfigureAwait(true);
        Assert.Equal(0, applied);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
    }

    /// <summary>管理要求の固定先を作成します。</summary>
    /// <returns>試験用の元要求です。</returns>
    private static ManagementCommand Command(string operation) => new(Guid.NewGuid(), "cluster", "peer", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), operation, null);
}
