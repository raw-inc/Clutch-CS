using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>生成前と確定直前のOS共有ロックを、容量・権限エラーとは区別します。</summary>
public sealed class AiOutputLockTests
{
    /// <summary>開始前にロック中なら生成要求を送らず、設定どおり失敗または正常スキップにします。</summary>
    /// <returns>ファイルを一度も書き換えない確認です。</returns>
    [Theory]
    [InlineData("error", "overwrite", EOutcome.NotExecuted)]
    [InlineData("skip", "overwrite", EOutcome.Succeeded)]
    [InlineData("error", "skip", EOutcome.Succeeded)]
    public async Task LockedFileIsHandledBeforeApiAsync(string locked, string exists, EOutcome expected)
    {
        using MediaTestFiles files = new();
        string output = Path.Combine(files.Root, "locked.wav");
        await File.WriteAllTextAsync(output, "original").ConfigureAwait(true);
        AiWireFixture server = new((_, _) => Task.FromResult(AiWireResponse.Json("{}")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = AiOutputPolicyTests.Settings("ai.audio.generate", "gpt-4o-mini-tts", output);
        settings["lockedFileMode"] = locked;
        settings["existingFileMode"] = exists;
        using (FileStream holder = new(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            NodeResult result = await AiOutputPolicyTests.ExecuteAsync(server, files, settings).ConfigureAwait(true);
            Assert.Equal(expected, result.Outcome);
            if (expected == EOutcome.Succeeded)
            {
                Assert.True(result.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
                Assert.Equal(output, Path.GetFullPath(result.Outputs["value"].Json.GetProperty("path").GetString()!));
            }
            else
            {
                Assert.Equal("ai.outputLocked", result.ReasonCode);
            }
        }
        Assert.Empty(server.Requests);
        Assert.Equal("original", await File.ReadAllTextAsync(output).ConfigureAwait(true));
    }

    /// <summary>生成要求後に出力がロックされた場合も旧ファイルを保護し、一時ファイルだけを除去します。</summary>
    /// <returns>送信済み要求の再試行なしに保存方針を適用する確認です。</returns>
    // 抑制理由: 実行途中に別のハンドルを開く試験です。finallyで応答を解放しpendingをawaitしてからfixtureを破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "生成とロック取得を重ね、finallyのawaitでfixtureの寿命内に必ず完了させます。")]
    [Theory]
    [InlineData("error", EOutcome.Failed)]
    [InlineData("skip", EOutcome.Succeeded)]
    public async Task LockAcquiredDuringGenerationUsesCommitPolicyAsync(string mode, EOutcome expected)
    {
        using MediaTestFiles files = new();
        byte[] wave = await File.ReadAllBytesAsync(await files.CreateAsync("audio").ConfigureAwait(true)).ConfigureAwait(true);
        string output = Path.Combine(files.Root, "late-lock.wav");
        await File.WriteAllTextAsync(output, "original").ConfigureAwait(true);
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AiWireFixture server = new(async (_, token) =>
        {
            requested.TrySetResult();
            await respond.Task.WaitAsync(token).ConfigureAwait(false);
            return new AiWireResponse(200, "audio/wav", wave);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = server.ConfigureAwait(true);
        Dictionary<string, object> settings = AiOutputPolicyTests.Settings("ai.audio.generate", "gpt-4o-mini-tts", output);
        settings["lockedFileMode"] = mode;
        settings["timeoutMs"] = 10000;
        Task<NodeResult> pending = AiOutputPolicyTests.ExecuteAsync(server, files, settings);
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            using FileStream holder = new(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            respond.TrySetResult();
            NodeResult result = await pending.ConfigureAwait(true);
            Assert.Equal(expected, result.Outcome);
            if (expected == EOutcome.Succeeded)
            {
                Assert.Equal("locked", result.Outputs["value"].Json.GetProperty("skipReason").GetString());
            }
            else
            {
                Assert.Equal("ai.outputLocked", result.ReasonCode);
            }
        }
        finally
        {
            respond.TrySetResult();
            _ = await pending.ConfigureAwait(true);
        }
        Assert.Single(server.Requests);
        Assert.Equal("original", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial"));
    }
}
