using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>ネイティブSQLiteの更新でも、利用可能なAPIと既定のSQL動作を維持します。</summary>
public sealed class NativeSqliteCompatibilityTests
{
    /// <summary>プロバイダーが呼び出す関数を、実際に読み込むネイティブライブラリで解決できます。</summary>
    [Fact]
    public void NativeLibraryExportsProviderEntryPoints()
    {
        Assembly provider = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;
        string library = "e_sqlite3";
        DllImportAttribute[] imports = provider.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<DllImportAttribute>()).OfType<DllImportAttribute>()
            .Where(import => string.Equals(import.Value, library, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(imports);
        nint handle = NativeLibrary.Load(library, provider, null);
        try
        {
            foreach (string? name in imports.Select(import => import.EntryPoint).Distinct(StringComparer.Ordinal))
            {
                Assert.NotNull(name);
                Assert.True(NativeLibrary.TryGetExport(handle, name, out _), name);
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>外部キー制約は、接続側で上書きしなくても有効です。</summary>
    [Fact]
    public void ForeignKeysRejectMissingParentsByDefault()
    {
        using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(parent_id INTEGER REFERENCES parent(id)); INSERT INTO child VALUES(1)";
        SqliteException error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT_FOREIGNKEY, error.SqliteExtendedErrorCode);
    }

    /// <summary>引用した識別子を文字列へ読み替えず、BLOBをLIKE検索の文字列として扱いません。</summary>
    [Fact]
    public void QuotedIdentifiersAndBlobMatchingRemainStrict()
    {
        using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"unknown_column\"";
        Assert.Equal(SQLitePCL.raw.SQLITE_ERROR, Assert.Throws<SqliteException>(() => command.ExecuteScalar()).SqliteErrorCode);
        command.CommandText = "SELECT CAST('saved' AS BLOB) LIKE 'saved'";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Equal(1000, SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_TRIGGER_DEPTH, -1));
    }

    /// <summary>配布済みの組込み拡張を、実際のSQLで利用できます。</summary>
    /// <param name="sql">表の作成・書込み・読取りを含む確認用SQLです。</param>
    // 抑制理由: SQLはこの試験の固定InlineDataだけから取得し、利用者の入力を含みません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "組込み拡張を検証する固定SQLだけを実行します。")]
    [Theory]
    [InlineData("SELECT json_extract('{\"value\":1}', '$.value')")]
    [InlineData("SELECT CAST(sqrt(1) AS INTEGER)")]
    [InlineData("SELECT CAST(geopoly_area('[[0,0],[1,0],[1,1],[0,1],[0,0]]') AS INTEGER)")]
    [InlineData("CREATE VIRTUAL TABLE document USING fts3(body); INSERT INTO document VALUES('saved'); SELECT count(*) FROM document WHERE document MATCH '(saved OR missing) AND saved'")]
    [InlineData("CREATE VIRTUAL TABLE document USING fts4(body); INSERT INTO document VALUES('saved'); SELECT count(*) FROM document WHERE document MATCH 'saved'")]
    [InlineData("CREATE VIRTUAL TABLE document USING fts5(body); INSERT INTO document VALUES('saved'); SELECT count(*) FROM document WHERE document MATCH 'saved'")]
    [InlineData("CREATE VIRTUAL TABLE bounds USING rtree(id, minimum, maximum); INSERT INTO bounds VALUES(1,0,1); SELECT count(*) FROM bounds WHERE minimum<=0.5 AND maximum>=0.5")]
    public void BuiltInExtensionsRemainAvailable(string sql)
    {
        using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.Equal(1L, command.ExecuteScalar());
    }
}
