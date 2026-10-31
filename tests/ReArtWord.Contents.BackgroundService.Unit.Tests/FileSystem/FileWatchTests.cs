using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>実OS通知とroot喪失後の同パス復帰を検証します。</summary>
public sealed class FileWatchTests
{
    /// <summary>rootが一度消えても同rootへ再listenし、新受付に新版を適用します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task WatchResumesSameRootAndAppliesRevisionAtEventAdmissionAsync()
    {
        using FileTestDirectory fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using CancellationTokenSource stop = new();
        string path = Path.Combine(fixture.Root, "watched");
        Directory.CreateDirectory(path);
        Channel<NodeEmission> emissions = Channel.CreateUnbounded<NodeEmission>();
        Channel<NodeSourceStatus> statuses = Channel.CreateUnbounded<NodeSourceStatus>();
        NodeExecutionContext current = FileTestDirectory.Context(new { path, filter = "*.txt" });
        NodeSourceContext context = new(current, () => current, (emission, token) => emissions.Writer.WriteAsync(emission, token), () => false, (status, token) => statuses.Writer.WriteAsync(status, token), stop.Token);
        string watchType = "file.watch";
        ISourceNode watcher = (ISourceNode)fixture.Nodes.Single(node => string.Equals(node.Descriptor.Type, watchType, StringComparison.Ordinal));
        Task<NodeResult> run = watcher.RunAsync(context, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = new TestTaskLifetime(run, timeout).ConfigureAwait(true);
        await WaitStatusAsync(statuses.Reader, ESourceState.Listening, timeout.Token).ConfigureAwait(true);
        string firstPath = Path.Combine(path, "first.txt");
        await File.WriteAllTextAsync(firstPath, "first", timeout.Token).ConfigureAwait(true);
        NodeEmission first = await WaitPathAsync(emissions.Reader, firstPath, timeout.Token).ConfigureAwait(true);
        Assert.Equal("revision", first.Context.Revision);
        Directory.Delete(path, true);
        await WaitStatusAsync(statuses.Reader, ESourceState.Unavailable, timeout.Token).ConfigureAwait(true);
        current = current with { Revision = "new" };
        Directory.CreateDirectory(path);
        await WaitStatusAsync(statuses.Reader, ESourceState.Listening, timeout.Token).ConfigureAwait(true);
        string secondPath = Path.Combine(path, "second.txt");
        await File.WriteAllTextAsync(secondPath, "second", timeout.Token).ConfigureAwait(true);
        NodeEmission second = await WaitPathAsync(emissions.Reader, secondPath, timeout.Token).ConfigureAwait(true);
        Assert.Equal("new", second.Context.Revision);
        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await run.ConfigureAwait(true)).Outcome);
    }

    /// <summary>通知の重複に依存せず指定状態を待ちます。</summary>
    /// <returns>到達完了です。</returns>
    private static async Task WaitStatusAsync(ChannelReader<NodeSourceStatus> reader, ESourceState expected, CancellationToken cancellationToken = default)
    {
        NodeSourceStatus status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        while (status.State != expected)
        {
            status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>OS固有の余分な通知を許容して対象パスの一件を待ちます。</summary>
    /// <returns>対象の取得結果です。</returns>
    private static async Task<NodeEmission> WaitPathAsync(ChannelReader<NodeEmission> reader, string path, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NodeEmission item = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            string valueName = "value";
            string pathName = "path";
            if (item.Result.Outputs.TryGetValue(valueName, out NodeValue? value) && string.Equals(value.Json.GetProperty(pathName).GetString(), path, StringComparison.Ordinal))
            {
                return item;
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }
}
