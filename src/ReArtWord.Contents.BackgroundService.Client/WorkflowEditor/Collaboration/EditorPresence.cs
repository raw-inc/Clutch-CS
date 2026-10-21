using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>操作時の座標共有を草稿の比較から分け、停止中は接続維持だけを低頻度で送ります。</summary>
internal sealed class EditorPresence(EditorDocument model, ManagementApi api)
{
    private readonly List<PresenceView> items = [];
    private NodePosition? latest, cursor, dragPosition;
    private string? dragNode, sent;
    private int generation = -1;
    private bool busy, realtime;
    private DateTimeOffset nextRead, keepAlive, movingUntil;
    private string visible = string.Empty;
    private EditorDraft? scopeBasis;
    private readonly List<string> dirtyNodes = [];
    private bool scopeKnown;

    /// <summary>ブラウザー実体を識別します。</summary>
    internal Guid Id
    {
        get;
    } = Guid.NewGuid();

    /// <summary>表示用の編集者名です。</summary>
    internal string Name
    {
        get; set;
    } = "編集画面";

    /// <summary>追加オプションで有効にした場合だけ移動途中の座標を送ります。</summary>
    internal bool Realtime
    {
        get => realtime;
        set
        {
            if (realtime == value)
            {
                return;
            }
            realtime = value;
            nextRead = default;
            if (!value)
            {
                dragNode = null;
                dragPosition = null;
            }
        }
    }

    /// <summary>同じUIの再接続をまとめた現在の表示です。</summary>
    internal IReadOnlyList<PresenceView> Items => items;

    /// <summary>表示位置が変わった場合だけ重ね表示を更新します。</summary>
    internal event Action? Changed;

    /// <summary>現在位置は保持し、既定ではクリック・変更操作だけを公開します。</summary>
    internal void SetPointer(NodePosition? point, string? node, NodePosition? position, bool interaction)
    {
        EnsureGeneration();
        latest = point;
        if (Realtime || interaction)
        {
            if (cursor != point || dragPosition != position)
            {
                movingUntil = api.UtcNow.AddSeconds(1);
            }
            cursor = point;
            dragNode = Realtime ? node
                                : null;
            dragPosition = Realtime ? position
                                    : null;
        }
    }

    /// <summary>キーボード操作でも最後の画面内位置を操作位置として共有します。</summary>
    internal void Interact() => SetPointer(latest, null, null, true);

    /// <summary>画面切替時の表示と送信基準を破棄します。</summary>
    internal void Clear()
    {
        items.Clear();
        sent = null;
        nextRead = keepAlive = default;
    }

    /// <summary>座標本文は変化時だけ、在席維持は20秒ごとに送ります。</summary>
    /// <returns>期限内の表示状態です。</returns>
    internal async Task RefreshAsync(string hostName, CancellationToken cancellationToken = default)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        EnsureGeneration();
        int currentGeneration = model.Generation;
        string id = model.Draft.Document.Id;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            DateTimeOffset now = api.UtcNow;
            if (!ReferenceEquals(scopeBasis, model.Draft))
            {
                scopeBasis = model.Draft;
                IReadOnlyList<string>? scope = EditorDirtyScope.Read(model.Draft);
                scopeKnown = scope is not null;
                dirtyNodes.Clear();
                dirtyNodes.AddRange(scope ?? []);
            }
            IReadOnlyList<string> selectedNodes = [];
            if (model.SelectedNode is { } selected)
            {
                selectedNodes = [selected];
            }
            IReadOnlyList<string>? scopeNodes = null;
            if (scopeKnown)
            {
                scopeNodes = dirtyNodes;
            }
            PresenceUpdate update = new(id, Id, hostName, Name, selectedNodes, cursor?.X, cursor?.Y,
                model.Draft.Dirty || model.HasInvalidInputs, scopeNodes, dragNode, dragPosition);
            string stamp = ContractJson.Hash(update);
            string path = "/api/collaboration/" + Uri.EscapeDataString(id) + "/presence";
            if (!string.Equals(stamp, sent, StringComparison.Ordinal) || keepAlive <= now)
            {
                await api.PostAsync<object>(path, update, timeout.Token).ConfigureAwait(false);
                if (currentGeneration != model.Generation)
                {
                    return;
                }
                sent = stamp;
                keepAlive = now.AddSeconds(20);
            }
            if (nextRead <= now)
            {
                IReadOnlyList<PresenceView>? incoming = await api.GetAsync<IReadOnlyList<PresenceView>>(path, timeout.Token).ConfigureAwait(false);
                if (currentGeneration != model.Generation)
                {
                    return;
                }
                int interval = 1000;
                if (Realtime && now < movingUntil)
                {
                    interval = 150;
                }
                nextRead = now.AddMilliseconds(interval);
                items.Clear();
                items.AddRange((incoming ?? []).Where(item => now < item.ExpiresAt)
                    .GroupBy(item => (item.Presence.WorkflowId, item.Presence.UiInstanceId))
                    .Select(group => group.MaxBy(item => item.ExpiresAt)!).OrderBy(item => item.Presence.UiInstanceId));
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // 通信失敗は編集取消として扱わず、確認済み表示の期限だけを守ります。
        }
        finally
        {
            busy = false;
            string snapshot = ContractJson.Hash(items.Where(item => item.Presence.UiInstanceId != Id && api.UtcNow < item.ExpiresAt).Select(item => item.Presence));
            if (!string.Equals(snapshot, visible, StringComparison.Ordinal))
            {
                visible = snapshot;
                movingUntil = api.UtcNow.AddSeconds(1);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>前のWFの座標・選択を新しいWFへ送信しません。</summary>
    private void EnsureGeneration()
    {
        if (generation != model.Generation)
        {
            generation = model.Generation;
            latest = cursor = dragPosition = null;
            dragNode = null;
            Clear();
        }
    }
}
