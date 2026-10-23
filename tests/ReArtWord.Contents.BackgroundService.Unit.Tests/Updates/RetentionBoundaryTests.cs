using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>読めても意味が不正な記録を保持し、他の世代の整理を継続できることを確認します。</summary>
public sealed class RetentionBoundaryTests
{
    /// <summary>更新入力と結果のどちらの配置が不正でも、その操作だけを除外します。</summary>
    /// <returns>不正資材の保持と、別の旧成功世代の回収確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidProgramInSavedRecordDoesNotBlockOtherGenerations(bool result)
    {
        using ManagedClientRetentionFixture fixture = new();
        WebClientUpdateJob invalid = await fixture.AddAsync(0);
        WebClientUpdateJob collect = await fixture.AddAsync(1);
        for (int generation = 2; generation <= 4; generation++)
        {
            await fixture.AddAsync(generation);
        }
        WebClientInstallation outside = fixture.Client with { ProgramDirectory = Path.GetTempPath() };
        if (result)
        {
            await fixture.SaveAsync(invalid.OperationId, "result.json", fixture.Result(invalid) with { WebClient = outside });
        }
        else
        {
            await fixture.SaveAsync(invalid.OperationId, "job.json", invalid with { Installation = outside });
        }
        using RetentionTraceListener diagnostics = new();
        Trace.Listeners.Add(diagnostics);
        try
        {
            await UpdateRetention.TrimManagedClientAsync(fixture.Program);
            Assert.All(fixture.Assets(invalid.OperationId), path => Assert.True(Directory.Exists(path)));
            Assert.All(fixture.Assets(collect.OperationId), path => Assert.False(Directory.Exists(path)));
            foreach (string record in new[] { "job.json", "result.json", "writer.json", "started" })
            {
                Assert.True(File.Exists(Path.Combine(fixture.Operation(invalid.OperationId), record)));
            }
            Assert.Contains(diagnostics.Messages, message => message.Contains(invalid.OperationId.ToString("N"), StringComparison.Ordinal)
                && message.Contains(nameof(InvalidDataException), StringComparison.Ordinal));
        }
        finally
        {
            Trace.Listeners.Remove(diagnostics);
        }
    }

    /// <summary>現在の導入記録を検証できない場合は、整理を止めて全資材を保持します。</summary>
    /// <returns>公開入口から例外が漏れず診断が出る確認です。</returns>
    [Fact]
    public async Task InvalidCurrentInstallationIsRetainedAndReported()
    {
        using ManagedClientRetentionFixture fixture = new();
        WebClientUpdateJob job = await fixture.AddAsync(0);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, WebClientCatalog.RecordName),
            System.Text.Json.JsonSerializer.Serialize(fixture.Client with { ProgramDirectory = Path.GetTempPath() }, ContractJson.Options));
        using RetentionTraceListener diagnostics = new();
        Trace.Listeners.Add(diagnostics);
        try
        {
            await UpdateRetention.TrimManagedClientAsync(fixture.Program);
            Assert.All(fixture.Assets(job.OperationId), path => Assert.True(Directory.Exists(path)));
            Assert.Contains(diagnostics.Messages, message => message.Contains(fixture.Program, StringComparison.Ordinal)
                && message.Contains(nameof(InvalidDataException), StringComparison.Ordinal));
        }
        finally
        {
            Trace.Listeners.Remove(diagnostics);
        }
    }
}

/// <summary>この試験中の整理診断を記録します。</summary>
internal sealed class RetentionTraceListener : TraceListener
{
    internal System.Collections.Concurrent.ConcurrentQueue<string> Messages
    {
        get;
    } = new();

    /// <summary>診断を受け取った順に保存します。</summary>
    public override void Write(string? message) => Messages.Enqueue(message ?? string.Empty);

    /// <summary>改行付きの診断も同じ保存先へ送ります。</summary>
    public override void WriteLine(string? message) => Write(message);
}
