using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Journal;

/// <summary>操作要求を送信前に保持し、結果不明を再実行しません。</summary>
internal sealed class OperationJournal(ManagementApi api, OperationJournalStorage storage, ConnectionState? connection = null)
{
    private readonly List<OperationRecord> items = [];
    private Task? initialization;

    /// <summary>このブラウザーに保持した操作です。</summary>
    public IReadOnlyList<OperationRecord> Items => items;

    /// <summary>別コンポーネントの操作による履歴変更を通知します。</summary>
    public event Action? Changed;

    /// <summary>表示する最終結果です。</summary>
    public JsonElement? LastResult
    {
        get; private set;
    }

    /// <summary>最終の操作IDです。</summary>
    public Guid? LastOperation
    {
        get; private set;
    }

    /// <summary>端末保存済みの要求を復元します。</summary>
    /// <returns>復元確認です。</returns>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (initialization is null || initialization.IsFaulted || initialization.IsCanceled)
        {
            return initialization = LoadCoreAsync(cancellationToken);
        }
        return initialization;
    }

    /// <summary>同時操作より先に一度だけ端末履歴を読み込みます。</summary>
    /// <returns>復元の完了です。</returns>
    private async Task LoadCoreAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OperationRecord> stored = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        items.Clear();
        items.AddRange(stored.OrderByDescending(item => item.CreatedAt));
        Changed?.Invoke();
    }

    /// <summary>元の型付き要求を保存してから一度だけ送信します。</summary>
    /// <returns>確認したAPI応答です。</returns>
    // 抑制理由: 保存障害へ送信有無を添え、例外型とstackを保って既存UI境界へ伝播します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW333", Justification = "送信前保存の障害を握り潰さず、表示情報だけを添えて呼出元UIへ返します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "保存例外の型とstackを維持し、送信していないことを呼出元へ通知するためです。")]
    public async Task<JsonElement> StartAsync<T>(Guid id, string kind, string path, string readPath, T request, string? recoverPath = null,
        string? targetPeer = null, string? targetCluster = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperationRecord item = new(id, kind, readPath, recoverPath, JsonSerializer.SerializeToElement(request, ContractJson.Options), DateTimeOffset.UtcNow,
            PeerId: targetPeer ?? api.SelectedPeerId ?? connection?.Current?.PeerId, ClusterId: targetCluster ?? api.SelectedClusterId);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            await storage.CreateAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            UiError.Attach(exception, "Operation {0} could not be recorded before sending. No request was sent. Check browser storage and query this ID if it already exists.", id);
            throw;
        }
        items.Insert(0, item);
        LastOperation = id;
        LastResult = null;
        Changed?.Invoke();
        JsonElement result = await api.PostForPeerAsync<JsonElement>(path, request, item.PeerId, item.ClusterId, item.OperationId, cancellationToken).ConfigureAwait(false);
        await ReceiveAsync(item, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>同じ操作IDの実行結果を再照会します。</summary>
    /// <returns>照会の完了です。</returns>
    public async Task ReadAsync(OperationRecord item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await ReceiveAsync(item, await api.GetForPeerAsync<JsonElement>(item.ReadPath, item.PeerId, item.ClusterId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>開始済み復元だけを元の要求で回復します。</summary>
    /// <returns>回復応答の確認です。</returns>
    public async Task RecoverAsync(OperationRecord item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RecoverPath is null)
        {
            throw UiError.Attach(new InvalidOperationException("Recovery is not available for this operation."), "Recovery is not available for this operation.");
        }
        await ReceiveAsync(item, await api.PostForPeerAsync<JsonElement>(item.RecoverPath, item.Request, item.PeerId, item.ClusterId, item.OperationId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>既知の操作を新しい実行に作り替えず照会します。</summary>
    /// <returns>結果照会の完了です。</returns>
    public async Task TrackAsync(string kind, Guid id, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (id == Guid.Empty)
        {
            throw UiError.Attach(new InvalidOperationException("The original operation ID is required."), "The original operation ID is required.");
        }
        string path = kind switch
        {
            "conflict" => "/api/conflicts/operations/",
            "capture" => "/api/snapshots/captures/",
            "restore" => "/api/snapshots/restores/",
            "update" => "/api/updates/",
            "run" => "/api/runs/",
            "cancel" => "/api/runs/cancellations/",
            "file" => "/api/files/operations/",
            "save" => "/api/workflows/operations/",
            "safety" => "/api/operations/",
            "script-package" => "/api/scripts/packages/operations/",
            "script-runtime" => "/api/scripts/runtimes/operations/",
            _ => throw UiError.Attach(new InvalidOperationException("Select an operation type."), "Select an operation type."),
        };
        OperationRecord? item = items.FirstOrDefault(entry => entry.OperationId == id);
        if (item is not null && !string.Equals(item.Kind, kind, StringComparison.Ordinal))
        {
            throw UiError.Attach(new InvalidOperationException("The requested operation type does not match the saved operation."), "The requested operation type does not match the saved operation.");
        }
        if (item is null)
        {
            item = new(id, kind, path + id, null, JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, PeerId: api.SelectedPeerId ?? connection?.Current?.PeerId, ClusterId: api.SelectedClusterId);
            item = await storage.TrackAsync(item, cancellationToken).ConfigureAwait(false);
            items.Insert(0, item);
            if (!string.Equals(item.Kind, kind, StringComparison.Ordinal))
            {
                throw UiError.Attach(new InvalidOperationException("The requested operation type does not match the saved operation."), "The requested operation type does not match the saved operation.");
            }
        }
        await ReadAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>業務未完了をHTTP成功へ潰しません。</summary>
    /// <returns>最後に確認した結果種類です。</returns>
    public static string Outcome(JsonElement? result)
    {
        if (result is not JsonElement value || value.ValueKind != JsonValueKind.Object)
        {
            return "unknown";
        }
        string completedKey = "completed";
        if (value.TryGetProperty(completedKey, out JsonElement completed) && completed.ValueKind == JsonValueKind.False)
        {
            return "unknown";
        }
        string outcomeKey = "outcome";
        if (value.TryGetProperty(outcomeKey, out JsonElement outcome))
        {
            return outcome.GetString() ?? "unknown";
        }
        string saveKey = "save";
        if (value.TryGetProperty(saveKey, out JsonElement save) && save.ValueKind == JsonValueKind.Object)
        {
            return Outcome(save);
        }
        string conflictsKey = "conflicts";
        if (value.TryGetProperty(conflictsKey, out JsonElement conflicts) && conflicts.ValueKind == JsonValueKind.Array && conflicts.GetArrayLength() != 0)
        {
            return "notExecuted";
        }
        string savedKey = "saved";
        if (value.TryGetProperty(savedKey, out JsonElement saved) && saved.ValueKind == JsonValueKind.Object)
        {
            string[] phases = ["saved", "shared", "snapshot", "activation"];
            string issuesKey = "issues";
            string snapshotKey = "snapshot";
            if (value.TryGetProperty(issuesKey, out _) && !value.TryGetProperty(snapshotKey, out _))
            {
                phases = ["saved", "shared", "activation"];
            }
            string success = "succeeded";
            string[] outcomes = phases.Select(phase =>
            {
                if (value.TryGetProperty(phase, out JsonElement phaseResult))
                {
                    return Outcome(phaseResult);
                }
                return "unknown";
            }).ToArray();
            string unknown = "unknown";
            if (outcomes.Contains(unknown, StringComparer.Ordinal))
            {
                return unknown;
            }
            if (outcomes.All(state => string.Equals(state, success, StringComparison.Ordinal)))
            {
                return success;
            }
            if (outcomes.Contains(success, StringComparer.Ordinal))
            {
                return "partial";
            }
            return outcomes[0];
        }
        string resultKey = "result";
        return value.TryGetProperty(resultKey, out JsonElement nested) ? Outcome(nested)
                                                                       : "unknown";
    }

    /// <summary>操作IDが一致する応答だけを保存します。</summary>
    /// <returns>保存の完了です。</returns>
    // 抑制理由: 受信後の保存障害を送信失敗にせず、元IDを添えて既存UI境界へ伝播します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW333", Justification = "受信後保存の障害を握り潰さず、再実行を避ける案内を添えて呼出元UIへ返します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "保存例外の型とstackを維持し、受信済みの操作を再送しないよう呼出元へ通知するためです。")]
    private async Task ReceiveAsync(OperationRecord item, JsonElement result, CancellationToken cancellationToken = default)
    {
        string identityKey = "operationId";
        string requestKey = "request";
        JsonElement identitySource = result;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(requestKey, out JsonElement request) && request.ValueKind == JsonValueKind.Object)
        {
            identitySource = request;
        }
        if (identitySource.ValueKind == JsonValueKind.Object && identitySource.TryGetProperty(identityKey, out JsonElement identity) && identity.GetGuid() != item.OperationId)
        {
            throw UiError.Attach(new InvalidOperationException("The response has a different operation ID. Its result cannot be confirmed."), "The response has a different operation ID. Its result cannot be confirmed.");
        }
        string saveKey = "save";
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(saveKey, out JsonElement save) && save.ValueKind == JsonValueKind.Object
            && (!save.TryGetProperty(identityKey, out JsonElement savedIdentity) || savedIdentity.ValueKind != JsonValueKind.String
                || !savedIdentity.TryGetGuid(out Guid savedId) || savedId != item.OperationId))
        {
            throw UiError.Attach(new InvalidOperationException("The original operation ID for the merged save does not match."), "The original operation ID for the merged save does not match.");
        }
        string conflictKind = "conflict";
        if (string.Equals(item.Kind, conflictKind, StringComparison.Ordinal) && (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty(identityKey, out JsonElement conflictId) || !conflictId.TryGetGuid(out Guid receivedId) || receivedId != item.OperationId))
        {
            throw UiError.Attach(new InvalidOperationException("The original conflict resolution result could not be confirmed."), "The original conflict resolution result could not be confirmed.");
        }
        int index = items.FindIndex(entry => entry.OperationId == item.OperationId);
        items[index] = item with { Result = result };
        LastOperation = item.OperationId;
        LastResult = result;
        try
        {
            OperationRecord saved = await storage.UpdateAsync(items[index], cancellationToken).ConfigureAwait(false);

            // 保存待機中に別操作が先頭へ追加されても、元IDの行だけを更新します。
            items[items.FindIndex(entry => entry.OperationId == item.OperationId)] = saved;
        }
        catch (JSException exception)
        {
            UiError.Attach(exception, "A response was received for operation {0}, but its latest result could not be saved in this browser. Query the original operation ID instead of running it again.", item.OperationId);
            throw;
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
