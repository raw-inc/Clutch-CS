using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>時計補正後の新しい完了と、再起動で読み直した履歴を区別します。</summary>
public sealed class WorkflowJournalClockTests
{
    /// <summary>UTCが戻っても新しい完了を保存し、再起動後の保存履歴は現在の活動へ数え直しません。</summary>
    /// <returns>JSONとSQLiteの実保存・復元の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalKeepsNewCompletionsAfterRollbackAndMarksRestoredAgeUnknownAsync(bool json)
    {
        using MediaTestFiles files = new();
        ResourceSelectionClock clock = new();
        string path = Path.Combine(files.Root, "state");
        IDurableRepository repository = json ? new JsonFileRepository(path)
                                             : new SqliteRepository(path);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, clock);
        using WorkflowExecutionJournal journal = new(repository, "a", engine, clock);
        CompiledWorkflow plan = fixture.Compiler.Compile(new("history", "History", [WorkflowFixture.Definition("action")], [])).Workflow!;
        for (int index = 0; index < 260; index++)
        {
            await engine.ExecuteAsync(plan).ConfigureAwait(true);
        }
        await journal.FlushAsync().ConfigureAwait(true);
        clock.Adjust(TimeSpan.FromHours(-1));
        ExecutionReport latest = await engine.ExecuteAsync(plan).ConfigureAwait(true);
        await journal.FlushAsync().ConfigureAwait(true);
        Assert.Null(journal.Problem);
        Assert.Contains(journal.Recent(), view => WorkflowExecutionJournal.Identity(view) == latest.Id);
        WorkflowEngine restarted = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, clock);
        using WorkflowExecutionJournal restored = new(repository, "a", restarted, clock);
        await restored.FlushAsync().ConfigureAwait(true);
        Assert.Null(restored.Problem);
        Assert.Equal(256, restored.Recent().Count);
        ExecutionResultView record = Assert.Single(restored.Recent(), view => WorkflowExecutionJournal.Identity(view) == latest.Id);
        Assert.True(record.CompletionAgeIsLowerBound);
        Assert.Equal(latest.CompletedAt, record.Report?.CompletedAt ?? record.Summary!.CompletedAt);
        Guid instance = Guid.NewGuid();
        ExecutionHistoryPage activity = ObservedExecutionHistory.Page(restored.Recent(), new(SummariesOnly: true), instance);
        Assert.Empty(activity.Entries);
        Assert.True(activity.AgeUnconfirmed);
        clock.Adjust(TimeSpan.FromDays(1));
        Assert.True(ObservedExecutionHistory.Page(restored.Recent(), new(SummariesOnly: true), instance).AgeUnconfirmed);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(ObservedExecutionHistory.Page(restored.Recent(), new(SummariesOnly: true), instance).AgeUnconfirmed);
        Assert.Contains(ExecutionHistory.Page(restored.Recent(), new()).Entries, view => WorkflowExecutionJournal.Identity(view) != Guid.Empty);
    }
}
