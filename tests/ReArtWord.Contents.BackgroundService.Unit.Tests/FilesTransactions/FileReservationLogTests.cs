using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>Traceを有効にしなくても、予約競合による未実行を警告へ残します。</summary>
public sealed class FileReservationLogTests
{
    /// <summary>成功を警告にせず、未実行と失敗を区別して秘密のない理由を記録します。</summary>
    /// <returns>実ランタイムを通したログと実施結果です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded, null)]
    [InlineData(EOutcome.NotExecuted, "WARN")]
    [InlineData(EOutcome.Failed, "ERROR")]
    public async Task FileAdmissionFailureIsVisibleWithoutTraceAsync(EOutcome outcome, string? level)
    {
        FileSystemOptions options = new([Path.GetTempPath()]);
        INode node = FileSystemNodeRegistration.Create(options).Single(item => item.Descriptor.Type is "file.read");
        WorkflowFixture fixture = new(node);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        RecordingLog log = new();
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, log)
        {
            FileExecutionHandler = (_, _, _) => ValueTask.FromResult(NodeResult.Failure("File.Overlap", "A conflicting operation owns the output.", outcome) with
            {
                Outputs = new Dictionary<string, NodeValue> { ["private"] = NodeValue.FromObject("A private path must not enter the log.") },
            }),
        };
        NodeDefinition definition = new("file", "file.read", JsonSerializer.SerializeToElement(new { path = Path.Combine(Path.GetTempPath(), "reservation-log-fixture.txt") }));
        RemoteExecutionRequest request = new(Guid.CreateVersion7(), Guid.NewGuid(), "files", "revision", definition, new Dictionary<string, NodeValue>(),
            fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), false);
        NodeResult result = await executor.ExecuteAsync(request).ConfigureAwait(true);
        Assert.Equal(outcome, result.Outcome);
        if (level is null)
        {
            Assert.Empty(log.Entries);
        }
        else
        {
            WorkflowLogEntry entry = Assert.Single(log.Entries);
            Assert.Equal(level, entry.Level);
            Assert.Equal("File.Overlap", entry.Message);
            Assert.NotNull(entry.Data);
            Assert.Equal((int)outcome, entry.Data.Json.GetProperty("Outcome").GetInt32());
            Assert.Equal("A conflicting operation owns the output.", entry.Data.Json.GetProperty("Message").GetString());
            Assert.Equal(request.Id, entry.Data.Json.GetProperty("executionId").GetGuid());
            Assert.Equal(request.Revision, entry.Data.Json.GetProperty("revision").GetString());
            Assert.Equal("File.Overlap", entry.Data.Json.GetProperty("ReasonCode").GetString());
            Assert.False(entry.Data.Json.TryGetProperty(nameof(NodeResult.Outputs), out _));
            Assert.DoesNotContain("A private path must not enter the log.", entry.Data.Json.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(request.CausationId, entry.CausationId);
        }
    }

    /// <summary>ログ境界で観測した実エントリーだけを保持します。</summary>
    private sealed class RecordingLog : IWorkflowLog
    {
        internal List<WorkflowLogEntry> Entries
        {
            get;
        } = [];

        /// <summary>非阻害な記録境界を模して受信します。</summary>
        /// <returns>受付済みです。</returns>
        public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }
}
