using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Database;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Database;

/// <summary>独立した実PostgreSQL・Redisで、業務値と失敗境界を確認します。</summary>
public sealed class ExternalDatabaseTests
{
    /// <summary>別接続からcommit結果を取得し、出力上限時には通常SQLをrollbackします。</summary>
    /// <returns>パラメーター化・型・rollback・期限の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_POSTGRESQL")]
    public async Task PostgreSqlCommitsParameterizedValuesAndRejectsPartialResultsAsync()
    {
        string table = "contents_test_" + Guid.NewGuid().ToString("N");
        IActionNode node = Node("database.postgresql", Environment.GetEnvironmentVariable("CONTENTS_TEST_POSTGRESQL"));
        Assert.Equal(EOutcome.Succeeded, (await QueryAsync(node, $"CREATE TABLE {table} (id BIGINT, value TEXT, data BYTEA)", "execute").ConfigureAwait(true)).Outcome);
        try
        {
            string text = "'); DROP TABLE anything; -- 日本語";
            NodeResult write = await QueryAsync(node, $"INSERT INTO {table} VALUES (@id,@value,@data)", "execute", new { id = 1, value = text, data = "AAEC" }, new { data = "binary" }).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, write.Outcome);
            NodeResult read = await QueryAsync(node, $"SELECT * FROM {table}", "query").ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, read.Outcome);
            Assert.Equal(text, read.Outputs["value"].Json[0].GetProperty("value").GetString());
            Assert.Equal("AAEC", read.Outputs["value"].Json[0].GetProperty("data").GetString());
            NodeResult over = await QueryAsync(node, $"INSERT INTO {table} (id) VALUES (2),(3) RETURNING id", "query", maxRows: 1).ConfigureAwait(true);
            Assert.Equal(EOutcome.Unknown, over.Outcome);
            Assert.Empty(over.Outputs);
            read = await QueryAsync(node, $"SELECT * FROM {table}", "query").ConfigureAwait(true);
            Assert.Single(read.Outputs["value"].Json.EnumerateArray());
            Assert.Equal(EOutcome.Unknown, (await QueryAsync(node, "SELECT pg_sleep(10)", "query", timeoutMs: 200).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            Assert.Equal(EOutcome.Succeeded, (await QueryAsync(node, $"DROP TABLE {table}", "execute").ConfigureAwait(true)).Outcome);
        }
    }

    /// <summary>実Redisで認証・DB選択・JSON/binary・キー欠落を確認します。</summary>
    /// <returns>通常のノード入力からの読書き結果です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_REDIS_PORT")]
    public async Task RedisUsesAuthenticatedConnectionAndPreservesValuesAsync()
    {
        IActionNode node = Node("database.redis", "contents-test-only");
        string key = "contents-test:" + Guid.NewGuid().ToString("N") + "\r\nGET other";
        try
        {
            Assert.Equal(EOutcome.Succeeded, (await RedisAsync(node, key, "set", "json", NodeValue.FromObject(new { text = "日本語", value = 12 })).ConfigureAwait(true)).Outcome);
            NodeResult read = await RedisAsync(node, key, "get", "json").ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, read.Outcome);
            Assert.True(read.Outputs["exists"].Json.GetBoolean());
            Assert.Equal("日本語", read.Outputs["value"].Json.GetProperty("text").GetString());
            Assert.Equal(EOutcome.Succeeded, (await RedisAsync(node, key, "set", "binary", NodeValue.FromBytes(new byte[] { 0, 255, 13, 10 })).ConfigureAwait(true)).Outcome);
            read = await RedisAsync(node, key, "get", "binary").ConfigureAwait(true);
            Assert.Equal(new byte[] { 0, 255, 13, 10 }, read.Outputs["value"].Binary.ToArray());
            read = await RedisAsync(node, key, "get", "binary", maxBytes: 3).ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, read.Outcome);
            Assert.Equal(EOutcome.Succeeded, (await RedisAsync(node, key, "set", "binary", NodeValue.FromBytes(new byte[128])).ConfigureAwait(true)).Outcome);
            read = await RedisAsync(node, key, "get", "binary", maxBytes: 80).ConfigureAwait(true);
            Assert.Equal(EOutcome.Unknown, read.Outcome);
            IActionNode rejected = Node("database.redis", "incorrect-password");
            Assert.Equal(EOutcome.NotExecuted, (await RedisAsync(rejected, key, "get", "binary").ConfigureAwait(true)).Outcome);
        }
        finally
        {
            Assert.Equal(EOutcome.Succeeded, (await RedisAsync(node, key, "delete", "text").ConfigureAwait(true)).Outcome);
        }
        NodeResult missing = await RedisAsync(node, key, "get", "text").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, missing.Outcome);
        Assert.False(missing.Outputs["exists"].Json.GetBoolean());
        Assert.False(missing.Outputs.ContainsKey("value"));
    }

    /// <summary>製品カタログに登録するノードを取得します。</summary>
    /// <returns>内部保存に依存しない実行ノードです。</returns>
    private static IActionNode Node(string type, string? secret) => (IActionNode)DatabaseNodeRegistration.Create(new TestSecretStore(secret), new([])).Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));

    /// <summary>実接続先とパラメーターを製品契約で渡します。</summary>
    /// <returns>SQLの結果です。</returns>
    private static async Task<NodeResult> QueryAsync(IActionNode node, string sql, string mode, object? parameters = null, object? parameterTypes = null, int maxRows = 100, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        if (parameters is not null)
        {
            inputs["parameters"] = NodeValue.FromObject(parameters);
        }
        return await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { secretId = "db", sql, mode, parameterTypes = parameterTypes ?? new { }, maxRows, timeoutMs }), extra: inputs), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>実Redis専用のループバックと秘密値参照を選びます。</summary>
    /// <returns>キー操作の結果です。</returns>
    private static async Task<NodeResult> RedisAsync(IActionNode node, string key, string operation, string format, NodeValue? value = null, int maxBytes = 1048576, CancellationToken cancellationToken = default)
    {
        JsonElement settings = SocketNodeTests.Settings(new { remoteAddress = "127.0.0.1", remotePort = int.Parse(Environment.GetEnvironmentVariable("CONTENTS_TEST_REDIS_PORT")!, CultureInfo.InvariantCulture), operation, format, secretId = "redis", database = 1, maxBytes });
        return await node.ExecuteAsync(NodeTestContext.Create(settings.GetRawText(), value, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["key"] = NodeValue.FromObject(key) }), cancellationToken).ConfigureAwait(true);
    }
}
