using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>自動解消のID生成方式を、結果履歴の有無ではなく原因版の保存世代で固定します。</summary>
public sealed class DerivedRootIdentityTests
{
    private const string Hash = "b1a15c0033108c77ac8e51522b813554c284b3d9c32a18382b41027075084558";

    /// <summary>旧版が準備済みの変更は旧方式のまま、新しい変更だけが複製先でも新方式になります。</summary>
    /// <returns>保存媒体の変更・再起動を跨ぐ方式の固定です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginGenerationSurvivesReplicationAndDoesNotRetagPreparedWritesAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(Path.Combine(files.Root, "source"), json);
        string oldClock = JsonSerializer.Serialize(new Dictionary<string, long> { [Guid.NewGuid().ToString("N")] = 1 });
        DurableMutation original = new("workflow:example", "old", null, NodeValue.FromObject(1), CausalVersion: oldClock);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [original]).ConfigureAwait(true)).Outcome);
        DurableEntry legacy = (await repository.ReadAsync(original.Scope, original.Key).ConfigureAwait(true))!;
        Assert.Equal(Guid.Parse("005ca1b1-1033-778c-ac8e-51522b813554"), await DerivedRootIdentity.CreateAsync(repository, [legacy], Hash).ConfigureAwait(true));

        DurableMutation next = new(original.Scope, "new", null, NodeValue.FromObject(2));
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [next]).ConfigureAwait(true)).Outcome);
        DurableEntry current = (await repository.ReadAsync(next.Scope, next.Key).ConfigureAwait(true))!;
        Guid expected = Guid.Parse("b1a15c00-3310-8c77-ac8e-51522b813554");
        Assert.Equal(expected, await DerivedRootIdentity.CreateAsync(repository, [current], Hash).ConfigureAwait(true));
        IDurableRepository other = Create(Path.Combine(files.Root, "replica"), !json);
        Assert.True(await other.ImportAsync(await repository.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.Equal(expected, await DerivedRootIdentity.CreateAsync(other, [current], Hash).ConfigureAwait(true));
        Assert.Equal(Guid.Parse("005ca1b1-1033-778c-ac8e-51522b813554"), await DerivedRootIdentity.CreateAsync(other, [legacy], Hash).ConfigureAwait(true));
    }

    /// <summary>独立した保存先を作り、JSONとSQLiteの間でも方式を維持します。</summary>
    /// <returns>試験専用の保存先です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path + ".json");
        }
        return new SqliteRepository(path + ".db");
    }
}
