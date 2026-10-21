using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>保存や有効化を行わず、未保存編集と一時的な編集位置を共有します。</summary>
public sealed class CollaborationService : ISnapshotEditor
{
    private readonly WorkflowStore workflows;
    private readonly PeerCluster cluster;
    private readonly TimeProvider clock;
    private readonly CollaborationOptions options;
    private readonly Lock gate = new();
    private readonly Dictionary<string, DraftSession> drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> queryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Peer, Guid Instance, Guid Ui, string Workflow), PresenceView> presence = [];
    private readonly CollaborationSnapshotEditor snapshotEditor;

    /// <summary>保存定義の読み取りと一時UDP通知を接続します。</summary>
    public CollaborationService(WorkflowStore workflows, PeerCluster cluster, TimeProvider? timeProvider = null, CollaborationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(cluster);
        this.workflows = workflows;
        this.cluster = cluster;
        clock = timeProvider ?? TimeProvider.System;
        this.options = options ?? new CollaborationOptions();
        snapshotEditor = new CollaborationSnapshotEditor(this, workflows, cluster);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumDrafts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumPresence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumChanges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumDraftBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.options.PresenceLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.options.DraftLifetime, TimeSpan.Zero);
        cluster.RegisterTransientHandler("collaboration.changes", ReceiveChangesAsync);
        cluster.RegisterTransientHandler("collaboration.presence", ReceivePresenceAsync);
        cluster.RegisterTransientHandler("collaboration.query", ReceiveQueryAsync);
        cluster.RegisterTransientHandler("collaboration.draft", ReceiveDraftAsync);
    }

    /// <summary>再接続時は他Peerにも未保存候補を照会します。</summary>
    /// <returns>保存版とは独立した現在下書きです。</returns>
    public async Task<DraftView?> GetDraftAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        StoredWorkflow? saved = await workflows.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        bool query;
        using (gate.EnterScope())
        {
            Prune();
            DateTimeOffset now = clock.GetUtcNow();
            query = !queryAfter.TryGetValue(workflowId, out DateTimeOffset next) || next <= now || TimeSpan.FromSeconds(5) < next - now;
            if (query)
            {
                if (!queryAfter.ContainsKey(workflowId) && options.MaximumDrafts <= queryAfter.Count)
                {
                    queryAfter.Remove(queryAfter.MinBy(item => item.Value).Key);
                }
                queryAfter[workflowId] = now.AddSeconds(5);
            }
            if (!drafts.ContainsKey(workflowId) && saved is not null)
            {
                AddDraft(new DraftSession(saved.Definition, saved.Revision, clock.GetUtcNow()));
            }
            if (drafts.TryGetValue(workflowId, out DraftSession? current))
            {
                if (saved is not null)
                {
                    current.Rebase(saved, clock.GetUtcNow());
                }
                current.Touch(clock.GetUtcNow());
            }
        }
        if (query)
        {
            await cluster.PublishTransientAsync("collaboration.query", NodeValue.FromObject(workflowId), cancellationToken).ConfigureAwait(false);
            if (cluster.GetPeers().Any(peer => peer.InstanceId != cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            }
        }
        using (gate.EnterScope())
        {
            return CopyView(workflowId);
        }
    }

    /// <summary>入力途中のgraphを三者比較し、確定保存をせず通知します。</summary>
    /// <returns>統合候補と未解消競合です。</returns>
    public async Task<DraftView> ApplyChangesAsync(DraftChangeRequest request, CancellationToken cancellationToken = default)
    {
        DraftView result = await ApplyLocalAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Validation.Count == 0)
        {
            DraftAnnouncement? announcement = null;
            using (gate.EnterScope())
            {
                if (drafts.TryGetValue(request.WorkflowId, out DraftSession? session))
                {
                    announcement = session.Announce(options.MaximumChanges, options.MaximumDraftBytes);
                }
            }
            if (announcement is not null)
            {
                await cluster.PublishTransientAsync("collaboration.draft", NodeValue.FromObject(announcement), cancellationToken).ConfigureAwait(false);
            }
        }
        return result;
    }

    /// <summary>自動反映の比較用に、未保存草稿を所有コピーで取得します。</summary>
    /// <returns>保持期限内の草稿です。保存や編集版の更新は行いません。</returns>
    public IReadOnlyList<DraftView> CaptureDrafts(bool changedOnly = false)
    {
        using (gate.EnterScope())
        {
            Prune();
            return drafts.Values.Where(session => !changedOnly || !string.Equals(session.View.BaseRevision, session.View.SessionVersion, StringComparison.Ordinal))
                .Select(session => ContractJson.Copy(session.View)).ToArray();
        }
    }

    /// <summary>再確認する一件だけを読み取り、他WFの本文を作り直しません。</summary>
    /// <returns>当該草稿、未保持ならnullです。</returns>
    public DraftView? CaptureDraft(string workflowId)
    {
        using (gate.EnterScope())
        {
            return CopyView(workflowId);
        }
    }

    /// <summary>復元開始前の選択範囲だけを確認します。</summary>
    /// <returns>未保存状態です。</returns>
    Task<SnapshotEditorState> ISnapshotEditor.InspectAsync(SnapshotEditLock scope, CancellationToken cancellationToken) => snapshotEditor.InspectAsync(scope, cancellationToken);

    /// <summary>確認した下書き版に対する保存確認または取消です。</summary>
    /// <returns>選択範囲の調停結果です。</returns>
    Task<ConfigurationPhaseResult> ISnapshotEditor.ResolveAsync(SnapshotDraftResolution request, SnapshotEditLock scope, CancellationToken cancellationToken) => snapshotEditor.ResolveAsync(request, scope, cancellationToken);

    /// <summary>UI実体を分けて編集位置を通知します。</summary>
    /// <returns>ローカル登録と一度の送信完了です。</returns>
    public Task UpdatePresenceAsync(PresenceUpdate update, CancellationToken cancellationToken = default)
    {
        ValidatePresence(update);
        return cluster.PublishTransientAsync("collaboration.presence", NodeValue.FromObject(update), cancellationToken);
    }

    /// <summary>期限切れの編集者を除いて取得します。</summary>
    /// <returns>別UI実体を保った現在表示です。</returns>
    public IReadOnlyList<PresenceView> GetPresence(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        using (gate.EnterScope())
        {
            Prune();
            return ContractJson.Copy(presence.Values.Where(item => string.Equals(item.Presence.WorkflowId, workflowId, StringComparison.Ordinal)).ToArray());
        }
    }

    /// <summary>保存版を必要時だけ読み、共有draftを更新します。</summary>
    /// <returns>下書き結果です。</returns>
    private async Task<DraftView> ApplyLocalAsync(DraftChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        if (request.OperationId == Guid.Empty || request.UiInstanceId == Guid.Empty || !string.Equals(request.WorkflowId, request.Original.Id, StringComparison.Ordinal) || !string.Equals(request.WorkflowId, request.Edited.Id, StringComparison.Ordinal) || options.MaximumDraftBytes < JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options).Length)
        {
            throw new ArgumentException("下書き対象または最大サイズが不正です。", nameof(request));
        }
        StoredWorkflow? saved = await workflows.GetAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (!await workflows.CanEditAsync(request.Original, request.Edited, cancellationToken).ConfigureAwait(false))
        {
            return new DraftView(request.WorkflowId, request.BaseRevision, ContractJson.Hash(request.Original), request.Original, [], [new ValidationIssue("draft.restoreLocked", "復元中の選択範囲は編集できません。")], clock.GetUtcNow());
        }
        using (gate.EnterScope())
        {
            Prune();
            if (!drafts.TryGetValue(request.WorkflowId, out DraftSession? session))
            {
                session = new DraftSession(saved?.Definition ?? request.Original, saved?.Revision, clock.GetUtcNow());
                AddDraft(session);
            }
            if (saved is not null)
            {
                session.Rebase(saved, clock.GetUtcNow());
            }
            return ContractJson.Copy(session.Apply(request, clock.GetUtcNow(), options.MaximumChanges, options.MaximumDraftBytes));
        }
    }

    /// <summary>受信した差分は再ブロードキャストせず同じ統合処理へ渡します。</summary>
    /// <returns>受信処理完了です。</returns>
    private async Task ReceiveChangesAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (instance != cluster.LocalInstanceId)
        {
            DraftChangeRequest request = value.Json.Deserialize<DraftChangeRequest>(ContractJson.Options)!;
            await ApplyLocalAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>受信元の実体を表示データから分離して記録します。</summary>
    /// <returns>登録完了です。</returns>
    private Task ReceivePresenceAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        PresenceUpdate update = value.Json.Deserialize<PresenceUpdate>(ContractJson.Options)!;
        ValidatePresence(update);
        using (gate.EnterScope())
        {
            Prune();
            presence[(peerId, instance, update.UiInstanceId, update.WorkflowId)] = new PresenceView(peerId, instance, ContractJson.Copy(update), clock.GetUtcNow() + options.PresenceLifetime);
            while (options.MaximumPresence < presence.Count)
            {
                presence.Remove(presence.MinBy(item => item.Value.ExpiresAt).Key);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>問い合わせに現在の未保存候補だけを返します。</summary>
    /// <returns>一度の応答送信です。</returns>
    private async Task ReceiveQueryAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (instance == cluster.LocalInstanceId)
        {
            return;
        }
        DraftAnnouncement? announcement = null;
        using (gate.EnterScope())
        {
            Prune();
            if (drafts.TryGetValue(value.Json.GetString()!, out DraftSession? session))
            {
                session.Touch(clock.GetUtcNow());
                announcement = session.Announce(options.MaximumChanges, options.MaximumDraftBytes);
            }
        }
        if (announcement is not null)
        {
            await cluster.PublishTransientAsync("collaboration.draft", NodeValue.FromObject(announcement), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>取りこぼした候補を優先度で上書きせず再比較します。</summary>
    /// <returns>受信完了です。</returns>
    private async Task ReceiveDraftAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        if (instance == cluster.LocalInstanceId)
        {
            return;
        }
        if (options.MaximumDraftBytes < JsonSerializer.SerializeToUtf8Bytes(value.Json).Length)
        {
            return;
        }
        DraftAnnouncement announcement = value.Json.Deserialize<DraftAnnouncement>(ContractJson.Options)!;
        if (!string.Equals(announcement.Original.Id, announcement.Draft.WorkflowId, StringComparison.Ordinal) || !string.Equals(announcement.Draft.Definition.Id, announcement.Draft.WorkflowId, StringComparison.Ordinal) || !string.Equals(announcement.Draft.SessionVersion, ContractJson.Hash(announcement.Draft.Definition), StringComparison.Ordinal))
        {
            return;
        }
        if (!await workflows.CanEditAsync(announcement.Original, announcement.Draft.Definition, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        StoredWorkflow? saved = await workflows.GetAsync(announcement.Draft.WorkflowId, cancellationToken).ConfigureAwait(false);
        using (gate.EnterScope())
        {
            Prune();
            if (!drafts.TryGetValue(announcement.Draft.WorkflowId, out DraftSession? session))
            {
                session = new DraftSession(announcement.Original, announcement.Draft.BaseRevision, clock.GetUtcNow());
                AddDraft(session);
            }
            session.MergeAnnouncement(announcement, clock.GetUtcNow(), options.MaximumChanges, options.MaximumDraftBytes);
            if (saved is not null)
            {
                session.Rebase(saved, clock.GetUtcNow());
            }
        }
    }

    /// <summary>UIに渡す値を内部候補から分離します。</summary>
    /// <returns>下書きのコピーです。</returns>
    private DraftView? CopyView(string workflowId)
    {
        if (drafts.TryGetValue(workflowId, out DraftSession? session))
        {
            return ContractJson.Copy(session.View);
        }
        return null;
    }

    /// <summary>最も古い下書きを退場させて保持量を守ります。</summary>
    private void AddDraft(DraftSession session)
    {
        drafts[session.View.WorkflowId] = session;
        while (options.MaximumDrafts < drafts.Count)
        {
            drafts.Remove(drafts.MinBy(item => item.Value.View.UpdatedAt).Key);
        }
    }

    /// <summary>通知や編集が止まった一時状態を退場させます。</summary>
    private void Prune()
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (KeyValuePair<(string Peer, Guid Instance, Guid Ui, string Workflow), PresenceView> entry in presence.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            presence.Remove(entry.Key);
        }
        foreach (string key in drafts.Where(item => item.Value.View.UpdatedAt + options.DraftLifetime <= now).Select(item => item.Key).ToArray())
        {
            drafts.Remove(key);
        }
    }

    /// <summary>一時表示に無制限の値を持ち込ませません。</summary>
    private static void ValidatePresence(PresenceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.WorkflowId);
        if (update.UiInstanceId == Guid.Empty || 256 < update.WorkflowId.Length || 256 < update.HostName.Length || 256 < update.DisplayName.Length || 256 < update.SelectedNodeIds.Count || update.SelectedNodeIds.Any(id => 256 < id.Length) || (update.CursorX is double x && !double.IsFinite(x)) || (update.CursorY is double y && !double.IsFinite(y))
            || 256 < update.DragNodeId?.Length || update.DragPosition is { } position && (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            || (update.DragNodeId is null) != (update.DragPosition is null))
        {
            throw new ArgumentException("編集者表示が不正です。", nameof(update));
        }
    }
}
