using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>対象キーの因果情報だけを本文と同じ取引から読みます。</summary>
internal static class SqliteCausality
{
    /// <summary>書込み位置と現在候補の索引を追加します。</summary>
    internal static void Load(SqliteSession session, RepositoryDocument document, IEnumerable<(string Scope, string Key)> keys)
    {
        Read(session, document, "position");
        foreach ((string scope, string key) in keys.Distinct())
        {
            if (DurableRepository.IsShared(scope))
            {
                Read(session, document, RepositoryCausality.EntryKey(scope, key));
                Read(session, document, "legacy:" + DurableRepository.GetAddress(scope, key));
            }
        }
    }

    /// <summary>必要な一行を読み、未保存の索引は作りません。</summary>
    private static void Read(SqliteSession session, RepositoryDocument document, string key)
    {
        string address = DurableRepository.GetAddress(RepositoryCausality.Scope, key);
        if (document.Entries.ContainsKey(address))
        {
            return;
        }
        DurableEntry? entry = session.Read<DurableEntry>("entry", address);
        if (entry is not null)
        {
            document.Entries[address] = entry;
        }
    }
}
