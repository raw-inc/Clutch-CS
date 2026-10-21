using System.Text.Json;
using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>画面所有者とクラスタ・Peerごとに草稿を保存します。</summary>
internal static class EditorDraftStorage
{
    private static readonly ConditionalWeakTable<EditorDraft, SerializedDraft> serialized = new();

    /// <summary>別タブの草稿を同じ編集所有範囲だけで列挙します。</summary>
    /// <returns>存在している草稿です。</returns>
    internal static async Task<IReadOnlyList<EditorDraft>> ReadAllAsync(BrowserStorage storage, Guid owner, string? cluster, string? peer, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DraftAddress> addresses = await storage.ReadAsync<IReadOnlyList<DraftAddress>>(IndexKey(owner), cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        List<EditorDraft> drafts = [];
        foreach (DraftAddress address in addresses.Where(address => string.Equals(address.Cluster, cluster, StringComparison.Ordinal) && string.Equals(address.Peer, peer, StringComparison.Ordinal)))
        {
            EditorDraft? draft = await ReadAsync(storage, owner, cluster, peer, address.Id, cancellationToken).ConfigureAwait(false);
            if (draft is not null)
            {
                drafts.Add(draft);
            }
        }
        return drafts;
    }

    /// <summary>reload前の所有者と接続先を読み、新しい画面の所有者へ引き継ぎます。</summary>
    /// <returns>接続先と最後の草稿です。</returns>
    internal static async Task<(Guid Owner, string? Cluster, string? Peer, EditorDraft? Draft)> InitializeAsync(BrowserStorage storage, Guid owner, CancellationToken cancellationToken = default)
    {
        string? cluster = await storage.ReadAsync<string>("contents.client.cluster", true, cancellationToken).ConfigureAwait(false);
        string? peer = await storage.ReadAsync<string>("contents.client.peer", true, cancellationToken).ConfigureAwait(false);
        Guid? previous = await storage.ReadAsync<Guid?>("contents.blazor.editor.owner", true, cancellationToken).ConfigureAwait(false);
        owner = await storage.ClaimDraftOwnerAsync(previous, owner, cancellationToken).ConfigureAwait(false);
        string? last = await ReadLastAsync(storage, cluster, peer, cancellationToken).ConfigureAwait(false);
        if (previous is Guid previousOwner && previousOwner != owner)
        {
            await CopyDraftsAsync(storage, previousOwner, owner, cancellationToken).ConfigureAwait(false);
        }
        await storage.WriteAsync("contents.blazor.editor.owner", owner, true, cancellationToken).ConfigureAwait(false);
        EditorDraft? draft = last is { Length: 0 } ? Navigation.EditorDraftNavigation.Empty()
                                                   : null;
        if (!string.IsNullOrEmpty(last) && previous is Guid old)
        {
            draft = await ReadAsync(storage, old, cluster, peer, last, cancellationToken).ConfigureAwait(false);
        }
        if (previous is null || draft is null && last is null)
        {
            draft = await LegacyDraftReader.MigrateAsync(storage, owner, cluster, peer, cancellationToken).ConfigureAwait(false);
        }
        return (owner, cluster, peer, draft);
    }

    /// <summary>Peer別の最後の表示対象を取得し、旧共有草稿の位置だけ移行します。</summary>
    /// <returns>最後のワークフロー識別子です。</returns>
    internal static async Task<string?> ReadLastAsync(BrowserStorage storage, string? cluster, string? peer, CancellationToken cancellationToken = default)
    {
        return await storage.ReadAsync<string>(LastKey(cluster, peer), true, cancellationToken).ConfigureAwait(false)
            ?? await storage.ReadAsync<string>("contents.blazor.editor.last." + JsonSerializer.Serialize(cluster), true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同じPeerの草稿を優先し、Peer不明の旧ローカル版は流用しません。</summary>
    /// <returns>該当する草稿またはnullです。</returns>
    internal static async Task<EditorDraft?> ReadAsync(BrowserStorage storage, Guid owner, string? cluster, string? peer, string id, CancellationToken cancellationToken = default)
    {
        EditorDraft? value = await storage.ReadAsync<EditorDraft>(Key(owner, cluster, peer, id), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (value is not null)
        {
            return value;
        }
        string legacyKey = "contents.blazor.draft." + owner.ToString("D") + "." + JsonSerializer.Serialize(new[] { cluster, id });

        // LEGACY-FORMAT: editor-cluster-draft-v1 — Peer別草稿への移行確認後に旧共有キーの読取りを撤去します。
        string? text = await storage.ReadTextAsync(legacyKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        EditorDraft? legacy = null;
        if (text is not null)
        {
            legacy = JsonSerializer.Deserialize<EditorDraft>(text, ContractJson.Options);
        }
        if (legacy?.LocalOnly == false)
        {
            if (await WriteAsync(storage, owner, cluster, peer, legacy, cancellationToken).ConfigureAwait(false) is null)
            {
                string? confirmed = await storage.ReadTextAsync(Key(owner, cluster, peer, id), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (string.Equals(confirmed, JsonSerializer.Serialize(legacy, ContractJson.Options), StringComparison.Ordinal))
                {
                    await storage.RemoveIfUnchangedAsync(legacyKey, text!, cancellationToken).ConfigureAwait(false);
                }
            }
            return legacy;
        }
        return null;
    }

    /// <summary>書込み不能を未保存理由へ変換し、呼出元に保持します。</summary>
    /// <returns>保存不能理由またはnullです。</returns>
    internal static async Task<string?> WriteAsync(BrowserStorage storage, Guid owner, string? cluster, string? peer, EditorDraft draft, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(draft.Document.Id))
            {
                await storage.WriteAsync(LastKey(cluster, peer), string.Empty, true, cancellationToken).ConfigureAwait(false);
                return null;
            }

            // 編集は新しいEditorDraftへ置き換えるため、同じ所有草稿の直列化だけを再利用します。
            string text = serialized.GetValue(draft, static value => new(JsonSerializer.Serialize(value, ContractJson.Options))).Text;
            await storage.WriteTextAsync(Key(owner, cluster, peer, draft.Document.Id), text, cancellationToken: cancellationToken).ConfigureAwait(false);
            List<DraftAddress> addresses = await storage.ReadAsync<List<DraftAddress>>(IndexKey(owner), cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
            DraftAddress address = new(cluster, peer, draft.Document.Id);
            if (!addresses.Contains(address))
            {
                addresses.Add(address);
                await storage.WriteAsync(IndexKey(owner), addresses, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            await storage.WriteAsync(LastKey(cluster, peer), draft.Document.Id, true, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Microsoft.JSInterop.JSException)
        {
            return "端末へ草稿を保存できません。画面を閉じる前に書き出してください。";
        }
    }

    /// <summary>明示削除したこの画面の草稿だけを索引から除きます。</summary>
    /// <returns>新しい画面へ削除済み草稿を引き継がない保存です。</returns>
    internal static async Task ForgetAsync(BrowserStorage storage, Guid owner, string? cluster, string? peer, string id, CancellationToken cancellationToken = default)
    {
        await storage.WriteAsync<object?>(Key(owner, cluster, peer, id), null, cancellationToken: cancellationToken).ConfigureAwait(false);
        string legacyKey = "contents.blazor.draft." + owner.ToString("D") + "." + JsonSerializer.Serialize(new[] { cluster, id });
        await storage.WriteAsync<object?>(legacyKey, null, cancellationToken: cancellationToken).ConfigureAwait(false);
        List<DraftAddress> addresses = await storage.ReadAsync<List<DraftAddress>>(IndexKey(owner), cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        addresses.Remove(new(cluster, peer, id));
        await storage.WriteAsync(IndexKey(owner), addresses, cancellationToken: cancellationToken).ConfigureAwait(false);
        await storage.WriteAsync<string?>(LastKey(cluster, peer), null, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>reload時は他Peerの未表示草稿も新画面所有者へコピーします。</summary>
    /// <returns>元所有者のデータを削除しない引継ぎです。</returns>
    private static async Task CopyDraftsAsync(BrowserStorage storage, Guid previous, Guid owner, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DraftAddress> addresses = await storage.ReadAsync<IReadOnlyList<DraftAddress>>(IndexKey(previous), cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        foreach (DraftAddress address in addresses)
        {
            EditorDraft? draft = await storage.ReadAsync<EditorDraft>(Key(previous, address.Cluster, address.Peer, address.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (draft is not null)
            {
                await storage.WriteAsync(Key(owner, address.Cluster, address.Peer, address.Id), draft, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        await storage.WriteAsync(IndexKey(owner), addresses, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>値を含まず画面が保管した草稿の場所だけを記録します。</summary>
    private sealed record DraftAddress(string? Cluster, string? Peer, string Id);

    /// <summary>草稿が参照されなくなれば一緒に回収する、同じ保存表現です。</summary>
    private sealed record SerializedDraft(string Text);

    /// <summary>同じ画面が保管した全接続先の索引です。</summary>
    /// <returns>ローカル索引キーです。</returns>
    private static string IndexKey(Guid owner) => "contents.blazor.editor.index." + owner.ToString("D");

    /// <summary>同名ワークフローのローカル版を分離するキーです。</summary>
    /// <returns>端末保存キーです。</returns>
    private static string Key(Guid owner, string? cluster, string? peer, string id) => "contents.blazor.draft." + owner.ToString("D") + "." + JsonSerializer.Serialize(new[] { cluster, peer, id });

    /// <summary>接続先ごとの最後の表示位置です。</summary>
    /// <returns>セッション保存キーです。</returns>
    private static string LastKey(string? cluster, string? peer) => "contents.blazor.editor.last." + JsonSerializer.Serialize(new[] { cluster, peer });
}
