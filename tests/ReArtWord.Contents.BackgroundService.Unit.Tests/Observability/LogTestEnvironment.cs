using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>実ファイル・リポジトリ・秘密値を試験ごとに隔離します。</summary>
internal sealed class LogTestEnvironment : IDisposable
{
    /// <summary>試験専用保存先を作成します。</summary>
    internal LogTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "ContentsLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Repository = new JsonFileRepository(Path.Combine(Root, "repository.json"));
        Secrets = new(Repository)
        {
            Replicator = Repository.ApplyBatchAsync,
        };
    }

    /// <summary>全試験資材のrootです。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>ログ本文専用の保存先です。</summary>
    internal string Logs => Path.Combine(Root, "logs");

    /// <summary>既存永続実装です。</summary>
    internal IDurableRepository Repository
    {
        get;
    }

    /// <summary>既存秘密保管です。</summary>
    internal SecretStore Secrets
    {
        get;
    }

    /// <summary>作成したrootだけを削除します。</summary>
    public void Dispose()
    {
        string prefix = Path.Combine(Path.GetTempPath(), "ContentsLogTests") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("試験保存先が不正です。");
        }
        Directory.Delete(Root, true);
    }
}
