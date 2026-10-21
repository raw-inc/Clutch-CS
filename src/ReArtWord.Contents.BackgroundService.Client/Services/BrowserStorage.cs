using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>ブラウザーの保存領域とファイル書出しだけを委譲します。</summary>
internal sealed class BrowserStorage(IJSRuntime script)
{
    /// <summary>再読み込みでは草稿領域を再利用し、稼働中の別タブだけを分離します。</summary>
    /// <returns>ブラウザーがこの画面へ確保した草稿所有者です。</returns>
    internal ValueTask<Guid> ClaimDraftOwnerAsync(Guid? previous, Guid proposed, CancellationToken cancellationToken = default)
        => script.InvokeAsync<Guid>("contentsEditorStore.owner", cancellationToken, previous, proposed);

    /// <summary>移行対象のブラウザー保存値を内容変更せず読みます。</summary>
    /// <returns>保存された生の文字列です。</returns>
    public ValueTask<string?> ReadTextAsync(string key, bool session = false, CancellationToken cancellationToken = default)
    {
        return script.InvokeAsync<string?>("contentsBrowser.read", cancellationToken, key, session);
    }

    /// <summary>端末に保持した型付き内容を取得します。</summary>
    /// <returns>保存値、または未保存です。</returns>
    public async ValueTask<T?> ReadAsync<T>(string key, bool session = false, CancellationToken cancellationToken = default)
    {
        string? text = await script.InvokeAsync<string?>("contentsBrowser.read", cancellationToken, key, session).ConfigureAwait(false);
        return text is null ? default
                            : JsonSerializer.Deserialize<T>(text, ContractJson.Options);
    }

    /// <summary>副作用送信前に元の要求を端末へ保存します。</summary>
    /// <returns>ブラウザー保存の完了です。</returns>
    public ValueTask WriteAsync<T>(string key, T value, bool session = false, CancellationToken cancellationToken = default)
    {
        return WriteTextAsync(key, JsonSerializer.Serialize(value, ContractJson.Options), session, cancellationToken);
    }

    /// <summary>所有済みの同じJSONを再生成せず、保存先の現在値を確認して書き込みます。</summary>
    /// <returns>ブラウザー保存の完了です。</returns>
    internal ValueTask WriteTextAsync(string key, string text, bool session = false, CancellationToken cancellationToken = default)
        => script.InvokeVoidAsync("contentsBrowser.write", cancellationToken, key, text, session);

    /// <summary>移行中に旧画面が変更した草稿を消さず、一致する旧本文だけを除去します。</summary>
    /// <returns>元本文が一致して削除できたかです。</returns>
    internal ValueTask<bool> RemoveIfUnchangedAsync(string key, string expected, CancellationToken cancellationToken = default)
        => script.InvokeAsync<bool>("contentsEditorStore.removeIfUnchanged", cancellationToken, key, expected);

    /// <summary>表示言語をアクセシビリティ用の文書属性へ反映します。</summary>
    /// <returns>ブラウザー文書属性の更新です。</returns>
    public ValueTask SetLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        return script.InvokeVoidAsync("contentsBrowser.language", cancellationToken, language);
    }

    /// <summary>正確なJSONをブラウザー downloadへ渡します。</summary>
    /// <returns>書出し要求の完了です。</returns>
    public ValueTask DownloadAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        return script.InvokeVoidAsync("contentsBrowser.download", cancellationToken, name, JsonSerializer.Serialize(value, ContractJson.Options));
    }
}
