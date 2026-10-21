using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Snapshots;

/// <summary>対象範囲を指定したスナップショットの取得と復元を管理します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class SnapshotPanel
{
    private static string MatchExclude => "exclude";
    private static string MatchAt => "at";
    private static string MatchOmit => "omit";

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get;
        set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get;
        set;
    } = null!;

    private bool captureOpen, restoreOpen, permanent;
    private ESnapshotKind kind;
    private string peer = string.Empty, target = string.Empty;
    private Func<string>? message = () => string.Empty;
    private string? at;
    private readonly List<SnapshotListSummary> snapshots = [];
    private string? snapshotCursor;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<SnapshotTarget> captureTargets = [];
    private readonly List<TargetRow> rows = [];
    private SnapshotRestoreRequest? pendingRestore;

    /// <summary>新しい取得は通常の保持期間を既定として開始します。</summary>
    private void OpenCapture()
    {
        permanent = false;
        captureOpen = true;
    }

    /// <summary>初回表示で必要な状態と継続監視を準備します。</summary>
    protected override void OnInitialized()
    {
        peer = Connection.Current?.PeerId ?? string.Empty;
    }

    /// <summary>操作の例外を確認不能の表示へ変換します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JSException)
        {
            message = exception is InvalidOperationException ? Ui.DeferredException(exception)
                                                             : Ui.Deferred("This result is unconfirmed. Query recorded requests in operation history.");
        }
    }

    /// <summary>対象ピアに対応するスナップショット操作情報を取得します。</summary>
    /// <returns>選択したピアと保存範囲を固定した対象指定です。</returns>
    private SnapshotTarget Target()
    {
        if (string.IsNullOrWhiteSpace(peer))
        {
            throw UiError.InvalidOperation("Specify a peer ID.");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            throw UiError.InvalidOperation("Specify a target ID.");
        }

        return new(target, peer, kind);
    }

    /// <summary>取得するスナップショットの対象範囲を展開します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task ExpandCaptureAsync(SnapshotScopeRequest request) => GuardAsync(async () => captureTargets = await ExpandAsync(request).ConfigureAwait(true));

    /// <summary>復元するスナップショットの対象範囲を展開します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task ExpandRestoreAsync(SnapshotScopeRequest request) => GuardAsync(async () =>
    {
        foreach (SnapshotTarget? item in await ExpandAsync(request).ConfigureAwait(true))
        {
            if (rows.All(row => row.Target != item))
            {
                rows.Add(new(item));
            }
        }

        message = Ui.Deferred("Expanded scopes were added. Explicitly choose a snapshot or time for each scope.");
    });

    /// <summary>明示された保存範囲を操作対象の集合へ展開します。</summary>
    /// <returns>対象ピアで解決したスナップショット範囲の一覧です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task<IReadOnlyList<SnapshotTarget>> ExpandAsync(SnapshotScopeRequest request)
    {
        SnapshotScopeResult? expanded = await Api.PostAsync<SnapshotScopeResult>("/api/snapshots/scope", request, cancellationToken: UiCancellation).ConfigureAwait(true);
        if (expanded is null)
        {
            throw UiError.InvalidOperation("Resolved targets could not be confirmed.");
        }

        if (expanded.Issues.Count != 0)
        {
            throw new InvalidOperationException(string.Join("\n", expanded.Issues.Select(issue => issue.Message)));
        }

        if (expanded.Targets.Count == 0)
        {
            throw UiError.InvalidOperation("No actual targets match these conditions.");
        }

        return expanded.Targets;
    }

    /// <summary>選択した保存範囲をスナップショット取得対象に設定します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task CaptureScopeAsync() => GuardAsync(async () =>
    {
        SnapshotTarget[]? fixedTargets = captureTargets.ToArray();
        bool fixedPermanent = permanent;
        captureOpen = false;
        foreach (SnapshotTarget? item in fixedTargets)
        {
            SnapshotCaptureRequest? request = new SnapshotCaptureRequest(Guid.CreateVersion7(), item, fixedPermanent);
            await Journal.StartAsync(request.OperationId, "Capture selected snapshot scope", "/api/snapshots/capture", "/api/snapshots/captures/" + request.OperationId, request, cancellationToken: UiCancellation).ConfigureAwait(true);
        }

        message = Ui.Deferred("Capture responses received for {0} targets. Check each operation’s actual result in history.", fixedTargets.Length);
    });

    /// <summary>固定した対象範囲のスナップショット取得を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task CaptureAsync() => GuardAsync(async () =>
    {
        SnapshotCaptureRequest? request = new SnapshotCaptureRequest(Guid.CreateVersion7(), Target(), permanent);
        captureOpen = false;
        await Journal.StartAsync(request.OperationId, "Capture snapshot", "/api/snapshots/capture", "/api/snapshots/captures/" + request.OperationId, request, cancellationToken: UiCancellation).ConfigureAwait(true);
        message = Ui.Deferred("Operation {0}: {1}", request.OperationId, OperationJournal.Outcome(Journal.LastResult));
    });

    /// <summary>選択したスナップショットの復元確認を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task OpenRestoreAsync() => GuardAsync(async () =>
    {
        SnapshotListPage? page = await Api.GetAsync<SnapshotListPage>("/api/snapshots", cancellationToken: UiCancellation).ConfigureAwait(true);
        if (page is null)
        {
            throw UiError.InvalidOperation("Snapshot candidates could not be retrieved.");
        }

        snapshots.Clear();
        rows.Clear();
        AddPage(page);
        restoreOpen = true;
    });

    /// <summary>スナップショット一覧の次ページを取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task NextSnapshotsAsync() => GuardAsync(async () =>
    {
        if (snapshotCursor is null)
        {
            return;
        }

        SnapshotListPage? page = await Api.PostAsync<SnapshotListPage>("/api/snapshots/query", new SnapshotListQuery(Cursor: snapshotCursor), cancellationToken: UiCancellation).ConfigureAwait(true);
        if (page is null)
        {
            throw UiError.InvalidOperation("The next snapshot page could not be confirmed.");
        }

        AddPage(page);
    });

    /// <summary>重複を避けて取得済みスナップショット一覧へページを加えます。</summary>
    private void AddPage(SnapshotListPage page)
    {
        foreach (SnapshotListSummary? snapshot in page.Entries)
        {
            if (snapshots.Any(item => string.Equals(item.IdentifierHash, snapshot.IdentifierHash, StringComparison.Ordinal)))
            {
                continue;
            }

            snapshots.Add(snapshot);
            if (snapshot.DetailReason is not null || snapshot.ScopeOmitted || snapshot.Id is null || snapshot.TargetId is null || snapshot.PeerId is null)
            {
                continue;
            }

            SnapshotTarget? value = new SnapshotTarget(snapshot.TargetId, snapshot.PeerId, snapshot.Kind, snapshot.NodeIds);
            if (rows.All(row => row.Target != value))
            {
                rows.Add(new(value));
            }
        }

        snapshotCursor = page.Cursor;
    }

    /// <summary>復元要求へ明示された対象を追加します。</summary>
    private void AddTarget()
    {
        try
        {
            SnapshotTarget? value = Target();
            if (rows.All(row => row.Target != value))
            {
                rows.Add(new(value));
            }
        }
        catch (InvalidOperationException)
        {
            message = Ui.Deferred("Specify a peer and target ID.");
        }
    }

    /// <summary>確認済み対象と方針を使って復元を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RestoreAsync() => GuardAsync(() =>
    {
        TargetRow[] selected = rows.Where(row => !string.Equals(row.Choice, MatchOmit, StringComparison.Ordinal)).ToArray();
        bool useTime = selected.Any(row => string.Equals(row.Choice, MatchAt, StringComparison.Ordinal));
        DateTimeOffset? point = useTime ? FormInput.ZonedTime(at, Ui.Format("display.timezone.point", DisplayTimeZone.Id), DisplayTimeZone)
                                        : null;
        if (selected.Length == 0 || selected.All(row => string.Equals(row.Choice, MatchExclude, StringComparison.Ordinal)) || (useTime && point is null))
        {
            throw UiError.InvalidOperation("Select at least one target to restore and enter a time for point-in-time selections.");
        }

        SnapshotSelection[] selections = selected.Select(row =>
        {
            bool excluded = string.Equals(row.Choice, MatchExclude, StringComparison.Ordinal);
            string? version = excluded || string.Equals(row.Choice, MatchAt, StringComparison.Ordinal) ? null
                                                                                                       : row.Choice;
            return new SnapshotSelection(row.Target, version, excluded);
        }).ToArray();
        pendingRestore = new SnapshotRestoreRequest(Guid.NewGuid(), selections, point);
        restoreOpen = false;
        return Task.CompletedTask;
    });

    /// <summary>指定時点での復元に使うローカル日時の入力を保持します。</summary>
    private void TimeChanged(ChangeEventArgs args) => at = args.Value?.ToString();

    /// <summary>復元先と採用するスナップショットの選択を保持します。</summary>
    private sealed class TargetRow(SnapshotTarget target)
    {
        /// <summary>復元操作の対象となるピアと保存範囲です。</summary>
        public SnapshotTarget Target
        {
            get;
        } = target;

        /// <summary>復元先に適用するスナップショットの選択です。</summary>
        public string Choice
        {
            get;
            set;
        } = "omit";

        /// <summary>編集中の行を識別する短い表示です。</summary>
        /// <returns>行の識別に必要な表示値です。</returns>
        public override string ToString() => $"{Target.PeerId}/{Target.Id}: {Choice}";
    }
}
