using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Database;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Database;

/// <summary>実SQLiteの保存・パラメーター化と取消、部分結果拒否を検証します。</summary>
[Collection("Performance")]
public sealed class RelationalNodeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "contents-sqlite-node", Guid.NewGuid().ToString("N"));

    /// <summary>試験専用の許可ディレクトリーを作成します。</summary>
    public RelationalNodeTests() => Directory.CreateDirectory(root);

    /// <summary>引用符やSQL風の入力をデータとして保持し、次の接続から取得できます。</summary>
    /// <returns>commit済み行と型の確認です。</returns>
    [Fact]
    public async Task ParameterizedWritePersistsAcrossConnectionsAsync()
    {
        Assert.Equal(EOutcome.Succeeded, (await ExecuteAsync("CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT, data BLOB)", "execute").ConfigureAwait(true)).Outcome);
        string text = "'); DROP TABLE items; --";
        NodeResult saved = await ExecuteAsync("INSERT INTO items VALUES (@id, @value, @data)", "execute", new { id = 1, value = text, data = "AAEC" }, new { data = "binary" }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Outcome);
        Assert.Equal(1, saved.Outputs["affected"].Json.GetInt32());
        NodeResult read = await ExecuteAsync("SELECT * FROM items", "query").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, read.Outcome);
        JsonElement row = Assert.Single(read.Outputs["value"].Json.EnumerateArray());
        Assert.Equal(text, row.GetProperty("value").GetString());
        Assert.Equal("AAEC", row.GetProperty("data").GetString());
    }

    /// <summary>書込みRETURNINGの出力が上限を超える場合、通常トランザクションはrollbackされます。</summary>
    /// <returns>部分成功と確定済みを装わない確認です。</returns>
    [Fact]
    public async Task OutputLimitRollsBackAndDoesNotExposePartialRowsAsync()
    {
        Assert.Equal(EOutcome.Succeeded, (await ExecuteAsync("CREATE TABLE items (value INTEGER)", "execute").ConfigureAwait(true)).Outcome);
        NodeResult result = await ExecuteAsync("INSERT INTO items VALUES (1),(2) RETURNING value", "query", maxRows: 1).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Empty(result.Outputs);
        NodeResult read = await ExecuteAsync("SELECT * FROM items", "query").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, read.Outcome);
        Assert.Empty(read.Outputs["value"].Json.EnumerateArray());
    }

    /// <summary>大量再帰SQLは全結果を待たずVMの期限で中断できます。</summary>
    /// <returns>戻りと後続接続の利用確認です。</returns>
    [Fact]
    public async Task DeadlineInterruptsSqliteVirtualMachineAsync()
    {
        Assert.Equal(EOutcome.Succeeded, (await ExecuteAsync("SELECT 1", "query").ConfigureAwait(true)).Outcome);
        NodeResult result = await ExecuteAsync("WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n", "query", timeoutMs: 1000).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Empty(result.Outputs);
        NodeResult subsequent = await ExecuteAsync("SELECT 42 AS answer", "query").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, subsequent.Outcome);
        Assert.Equal(42, subsequent.Outputs["value"].Json[0].GetProperty("answer").GetInt32());
    }

    /// <summary>許可範囲外の接続は開始しません。</summary>
    /// <returns>実ファイルを生成しない確認です。</returns>
    [Fact]
    public async Task OutsideAllowedDirectoryDoesNotExecuteAsync()
    {
        IActionNode node = Node();
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { path = Path.Combine(root, "..", "outside.sqlite"), sql = "SELECT 1", createIfMissing = true }))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
    }

    /// <summary>所有する一時ディレクトリーだけを片付けます。</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>内部保存に依存しないSQLiteノードを作成します。</summary>
    /// <returns>通常登録された実行ノードです。</returns>
    private IActionNode Node() => (IActionNode)DatabaseNodeRegistration.Create(new TestSecretStore(), new([root])).Single(node => string.Equals(node.Descriptor.Type, "database.sqlite", StringComparison.Ordinal));

    /// <summary>通常の設定・入力契約でSQLを実行します。</summary>
    /// <returns>製品が返した結果です。</returns>
    private async Task<NodeResult> ExecuteAsync(string sql, string mode, object? parameters = null, object? parameterTypes = null, int maxRows = 100, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (parameters is not null)
        {
            inputs["parameters"] = NodeValue.FromObject(parameters);
        }
        string settings = JsonSerializer.Serialize(new { path = Path.Combine(root, "business.sqlite"), createIfMissing = true, sql, mode, parameterTypes = parameterTypes ?? new { }, maxRows, timeoutMs });
        return await Node().ExecuteAsync(NodeTestContext.Create(settings, extra: inputs), cancellationToken).ConfigureAwait(true);
    }
}
