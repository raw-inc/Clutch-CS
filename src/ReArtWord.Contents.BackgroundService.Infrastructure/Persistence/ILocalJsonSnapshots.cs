using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>呼出中に固定された診断値を、巨大なJSON文書を作らず保存する経路です。</summary>
internal interface ILocalJsonSnapshots
{
    /// <summary>既存ローカルJSONを巨大な中間文書へ変換せず復元します。</summary>
    /// <returns>復元値、未保存または削除済みならnullです。</returns>
    internal ValueTask<T?> ReadJsonAsync<T>(string scope, string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>writerは複数回呼ばれても同じ値を出力し、完了まで入力を変更しません。</summary>
    /// <returns>ローカルスナップショットの原子的な保存結果です。</returns>
    internal ValueTask<EOutcome> WriteJsonAsync(string scope, string key, Action<Utf8JsonWriter> writeValue, CancellationToken cancellationToken = default);
}

/// <summary>対応媒体には直接書込みを渡し、既存媒体では同じNodeValue形式へ戻します。</summary>
internal static class LocalJsonSnapshots
{
    /// <summary>対応媒体では型付き値を直接読み、旧媒体にも同じ意味を適用します。</summary>
    /// <returns>ローカルスナップショットの復元値です。</returns>
    internal static async ValueTask<T?> ReadAsync<T>(IDurableRepository repository, string scope, string key, CancellationToken cancellationToken = default) where T : class
    {
        if (repository is ILocalJsonSnapshots streaming)
        {
            return await streaming.ReadJsonAsync<T>(scope, key, cancellationToken).ConfigureAwait(false);
        }
        return Decode<T>(await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>従来媒体のJSONと欠損の意味を直接復元と揃えます。</summary>
    /// <returns>ローカルスナップショットの型付き値です。</returns>
    internal static T? Decode<T>(DurableEntry? entry) where T : class
    {
        if (entry?.Value is null)
        {
            return null;
        }
        if (entry.Value.Kind != EValueKind.Json)
        {
            throw new InvalidDataException("局所診断snapshotがJSONではありません。");
        }
        return entry.Value.Json.Deserialize<T>(ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Options)
            ?? throw new InvalidDataException("局所診断snapshotが欠損しています。");
    }

    /// <summary>保存先の選択を保ちながらJSON文書の不要な保持を避けます。</summary>
    /// <returns>保存先が確認した結果です。</returns>
    internal static ValueTask<EOutcome> WriteAsync(IDurableRepository repository, string scope, string key, Action<Utf8JsonWriter> writeValue, CancellationToken cancellationToken = default)
    {
        if (repository is ILocalJsonSnapshots streaming)
        {
            return streaming.WriteJsonAsync(scope, key, writeValue, cancellationToken);
        }
        return repository.WriteLocalSnapshotAsync(scope, key, Materialize(writeValue), cancellationToken);
    }

    /// <summary>直接書込み非対応の保存形式にだけ、独立した従来値を構築します。</summary>
    /// <returns>保存先へ所有権を渡せるJSON値です。</returns>
    internal static NodeValue Materialize(Action<Utf8JsonWriter> writeValue)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writeValue(writer);
        }
        using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        return NodeValue.FromJson(document.RootElement);
    }
}
