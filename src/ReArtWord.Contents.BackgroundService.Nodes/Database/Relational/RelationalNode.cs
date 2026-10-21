using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Npgsql;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database.Relational;

/// <summary>内部保存とは独立した一回の業務SQLを、明示トランザクションで実行します。</summary>
internal sealed class RelationalNode : IActionNode
{
    private readonly bool postgres;
    private readonly ISecretStore secrets;
    private readonly FileAccessPolicy files;

    /// <summary>接続先の役割を固定し、秘密値とローカル許可範囲を使用します。</summary>
    internal RelationalNode(bool postgres, ISecretStore secrets, FileSystemOptions options)
    {
        this.postgres = postgres;
        this.secrets = secrets;
        files = new(options);
        string type = "database.sqlite";
        string required = "\"path\",\"sql\"";
        string connection = "\"path\":{\"type\":\"string\"},\"createIfMissing\":{\"type\":\"boolean\"},";
        if (postgres)
        {
            type = "database.postgresql";
            required = "\"secretId\",\"sql\"";
            connection = "\"secretId\":{\"type\":\"string\",\"minLength\":1},";
        }
        string schema = "{\"type\":\"object\",\"required\":[" + required + "],\"properties\":{" + connection
            + "\"sql\":{\"type\":\"string\",\"minLength\":1,\"x-multiline\":true},\"mode\":{\"type\":\"string\",\"enum\":[\"query\",\"execute\"]},\"parameterTypes\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\",\"enum\":[\"string\",\"integer\",\"number\",\"decimal\",\"boolean\",\"binary\",\"datetime\",\"guid\",\"json\"]}},\"timeoutMs\":{\"type\":[\"integer\",\"null\"],\"minimum\":0,\"maximum\":2147483646,\"default\":30000},\"maxRows\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000,\"default\":10000},\"maxBytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":67108864,\"default\":16777216}}}";
        Descriptor = new(type, type, "Database", "Execute parameterized SQL in one transaction. A commit with no confirmed response remains unknown and is not retried.",
            ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new("parameters", EPortValueKind.Json)], [new("value", EPortValueKind.Json), new("affected", EPortValueKind.Json)], ["success"], NodeSettings.Schema(schema)) { HasAsyncCompletion = true };
    }

    /// <summary>接続先とSQLの設定です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>SQLを実行せず、必要な設定を検証します。</summary>
    /// <returns>設定問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>出力の確認後にcommitし、失敗・補償・結果不明を区別します。</summary>
    /// <returns>行配列または変更件数です。</returns>
    // 抑制理由: 利用者が記述するSQLが製品の入力であり、値は別途パラメーター化します。
    [SuppressMessage("Security", "CA2100", Justification = "SQL自体は利用者が構成する処理です。入力値はRelationalParametersがパラメーターとして渡し、SQLへ連結しません。")]
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return invalid;
        }
        DbTransaction? transaction = null;
        DbConnection? connection = null;
        bool sent = false, committing = false;
        try
        {
            NodeSettings settings = new(context.Settings);
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
            connection = await CreateConnectionAsync(settings, deadline.Token).ConfigureAwait(false);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = settings.ReadText("sql")!;
            command.CommandTimeout = 0;
            if (!postgres)
            {
                command.CommandTimeout = SqliteExecutionLimits.Timeout(settings);
            }
            NodeValue? parameters = NodeSupport.Input(context, "parameters");
            RelationalParameters.Bind(command, parameters?.Json ?? default, settings.Get("parameterTypes"));
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
            if (connection is SqliteConnection sqlite)
            {
                SqliteExecutionLimits.Configure(sqlite, settings, deadline.Token);
            }
            transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            command.Transaction = transaction;
            string query = "query";
            string mode = settings.ReadText("mode", query)!;
            int affected;
            NodeValue value;
            sent = true;
            if (string.Equals(mode, query, StringComparison.Ordinal))
            {
                using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, deadline.Token).ConfigureAwait(false);
                JsonArray rows = await RelationalRows.ReadAsync(reader, settings.ReadInt32("maxRows", 10000), settings.ReadInt32("maxBytes", 16777216), deadline.Token).ConfigureAwait(false);
                affected = reader.RecordsAffected;
                value = NodeValue.FromObject(rows);
            }
            else
            {
                affected = await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                value = NodeValue.FromObject(new { affected });
            }
            committing = true;
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value, ["affected"] = NodeValue.FromObject(affected) });
        }
        catch (Exception exception) when (exception is DbException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or OverflowException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (sent)
            {
                outcome = EOutcome.Unknown;
            }
            if (transaction is not null && !committing)
            {
                try
                {
                    using CancellationTokenSource rollback = new(TimeSpan.FromSeconds(5));
                    await transaction.RollbackAsync(rollback.Token).ConfigureAwait(false);
                    if (exception is PostgresException { SqlState: not PostgresErrorCodes.QueryCanceled } or SqliteException { SqliteErrorCode: not 9 })
                    {
                        outcome = EOutcome.Failed;
                    }

                    // FailedはSQLの拒否確認であり、SQLから呼ぶ外部処理まで取消済みとは断定しません。
                }
                catch (Exception rollbackError) when (rollbackError is DbException or InvalidOperationException or OperationCanceledException)
                {
                    outcome = EOutcome.Unknown;
                }
            }
            return NodeResult.Failure("database.operationUnconfirmed", "DB処理またはcommitの完了を確認できません。SQLや接続秘密値は診断へ出力しません。", outcome);
        }
        finally
        {
            await CloseAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            await CloseAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>解放時の通信断で確認済みcommitを別結果へ置換せず、次の所有資源も解放します。</summary>
    /// <returns>所有する接続またはトランザクションの解放試行です。</returns>
    private static async Task CloseAsync(IAsyncDisposable? resource, CancellationToken cancellationToken = default)
    {
        try
        {
            if (resource is not null)
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is DbException or IOException or InvalidOperationException)
        {
            // 完了応答を得たSQLを、解放の失敗だけで未実行や再試行へ変えません。
        }
    }

    /// <summary>SQLiteは許可パス、PostgreSQLは今回解決した接続秘密値を使用します。</summary>
    /// <returns>呼出側が破棄する新しい接続です。</returns>
    private async Task<DbConnection> CreateConnectionAsync(NodeSettings settings, CancellationToken cancellationToken = default)
    {
        if (!postgres)
        {
            string path = files.ResolvePath(settings.ReadText("path")!);
            SqliteOpenMode mode = SqliteOpenMode.ReadWrite;
            bool create = settings.ReadBoolean("createIfMissing");
            if (create)
            {
                mode = SqliteOpenMode.ReadWriteCreate;
            }
            return new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = SqliteExecutionLimits.Timeout(settings) }.ToString());
        }
        string connectionString = await secrets.GetAsync(settings.ReadText("secretId")!, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("database.secretUnavailable");
        NpgsqlConnectionStringBuilder builder = new(connectionString)
        {
            Pooling = false, Enlist = false, PersistSecurityInfo = false, IncludeErrorDetail = false, LogParameters = false,
        };
        return new NpgsqlConnection(builder.ToString());
    }
}
