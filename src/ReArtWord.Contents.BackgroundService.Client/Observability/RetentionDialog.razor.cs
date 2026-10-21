using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Client.Observability;

/// <summary>ログ等の保持期間と容量制限を編集します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class RetentionDialog
{
    /// <summary>設定対象を選択していない間は編集を開始しません。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get; set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get; set;
    } = null!;

    private bool open, applyExisting, expand;
    private Func<string>? message;
    private LogOptions? options;
    private LogRetentionView? shared, observed;
    private string rollingTime = "00:00", timeZone = "UTC";
    private string defaultRaw = "7", defaultTotal = "30", checkpointEvents = "100", checkpointDays = "3";
    private bool defaultUnlimited;
    private string? statusPeer, cluster;
    private readonly List<RetentionRow> rows = [];
    private static readonly string[] logLevels = ["DEBUG", "INFO", "WARN", "ERROR", "FATAL"];
    private readonly List<HistoryCheckpointInfo> checkpoints = [];
    private HistoryArchiveStatus? historyStatus;

    /// <summary>対象の現在値を取得して編集または選択画面を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenAsync()
    {
        if (Disabled)
        {
            return;
        }
        try
        {
            options = await Api.GetAsync<LogOptions>("/api/logs/options", cancellationToken: UiCancellation).ConfigureAwait(true);
            shared = await Api.GetAsync<LogRetentionView>("/api/logs/retention", cancellationToken: UiCancellation).ConfigureAwait(true);
            observed = shared;
            checkpoints.Clear();
            statusPeer = Api.SelectedPeerId;
            cluster = Api.SelectedClusterId;
            (HistoryArchiveStatus? Status, HistoryCheckpointInfo[] Points) history = await ReadHistoryAsync(statusPeer, cluster, UiCancellation).ConfigureAwait(true);
            historyStatus = history.Status;
            checkpoints.AddRange(history.Points);
            LogRetentionSettings? policy = shared?.Configuration.Saved?.Settings.LogRetention;
            DataRetentionSettings? data = shared?.Configuration.Saved?.Settings.DataRetention;
            LogRetentionRule? baseline = (policy?.Rules ?? options?.Retention)?.FirstOrDefault(rule => string.Equals(rule.Level, "INFO", StringComparison.Ordinal));
            int rawDays = data?.RawDays ?? baseline?.RawDays ?? 7;
            int? totalDays = 30;
            if (data is not null)
            {
                totalDays = data.TotalDays;
            }
            if (data is null && baseline is not null)
            {
                totalDays = baseline.TotalDays;
            }
            defaultRaw = rawDays.ToString(CultureInfo.InvariantCulture);
            defaultTotal = (totalDays ?? 30).ToString(CultureInfo.InvariantCulture);
            defaultUnlimited = totalDays is null;
            checkpointEvents = (data?.CheckpointEvents ?? 100).ToString(CultureInfo.InvariantCulture);
            checkpointDays = (data?.CheckpointDays ?? 3).ToString(CultureInfo.InvariantCulture);
            rollingTime = data?.RollingTime ?? policy?.RollingTime ?? options?.RollingTime ?? "00:00";
            timeZone = data?.TimeZoneId ?? policy?.TimeZoneId ?? options?.TimeZoneId ?? "UTC";
            applyExisting = false;
            expand = false;
            rows.Clear();
            foreach (ERetentionCategory category in Enum.GetValues<ERetentionCategory>())
            {
                string level = category.ToString();
                LogRetentionRule? rule = null;
                string suffix = "Log";
                if (level.EndsWith(suffix, StringComparison.Ordinal))
                {
                    rule = (policy?.Rules ?? options?.Retention)?.FirstOrDefault(item => string.Equals(item.Level, level[..^3], StringComparison.OrdinalIgnoreCase));
                }
                RetentionWindow window = new(rawDays, totalDays);
                bool custom = rule is not null && (rule.RawDays != rawDays || rule.TotalDays != totalDays);
                if (rule is not null)
                {
                    window = new(rule.RawDays, rule.TotalDays);
                }
                if (data is not null)
                {
                    window = data.Window(category);
                    custom = data.Overrides?.ContainsKey(category) == true;
                }
                rows.Add(new()
                {
                    Category = category,
                    Level = level,
                    Override = custom,
                    RawDays = window.RawDays.ToString(CultureInfo.InvariantCulture),
                    TotalDays = (window.TotalDays ?? 30).ToString(CultureInfo.InvariantCulture),
                    Unlimited = window.TotalDays is null
                });
            }

            open = true;
        }
        catch (HttpRequestException)
        {
            message = Ui.Deferred("The retention policy could not be confirmed.");
        }
    }

    /// <summary>入力内容の保存を要求し、現時点の結果を画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SaveAsync()
    {
        try
        {
            if (options is null)
            {
                throw UiError.InvalidOperation("Retrieve the current retention settings first.");
            }

            int commonRaw = FormInput.Integer(defaultRaw, Ui["Common raw retention days"]);
            int? commonTotal = null;
            if (!defaultUnlimited)
            {
                commonTotal = FormInput.Integer(defaultTotal, Ui["Common total retention days"], commonRaw);
            }
            Dictionary<ERetentionCategory, RetentionWindow> overrides = rows.Where(row => row.Override).ToDictionary(row => row.Category, row =>
            {
                int raw = FormInput.Integer(row.RawDays, Ui.Format("{0} raw retention days", row.Level));
                int? total = row.Unlimited ? null
                                           : FormInput.Integer(row.TotalDays, Ui.Format("{0} total retention days", row.Level), raw);
                return new RetentionWindow(raw, total);
            });
            DataRetentionSettings data = new(commonRaw, commonTotal, overrides,
                FormInput.Integer(checkpointEvents, Ui["Checkpoint after committed changes"], 1), FormInput.Integer(checkpointDays, Ui["Checkpoint after days with changes"], 1),
                rollingTime, timeZone, applyExisting);
            data.Validate();
            LogRetentionRule[] retention = logLevels.Select(level =>
            {
                RetentionWindow window = data.Window(Enum.Parse<ERetentionCategory>(level + "Log", ignoreCase: true));
                return new LogRetentionRule(level, window.RawDays, window.TotalDays);
            }).ToArray();
            Guid id = Guid.CreateVersion7();
            LogOptionsChangeRequest request = new(options with
            {
                Retention = retention, RollingTime = rollingTime, TimeZoneId = timeZone
            }, applyExisting, expand, OperationId: id, ExpectedRevision: shared?.Configuration.Saved?.Revision, DataRetention: data);
            JsonElement response = await Journal.StartAsync(id, "Save shared log retention", "/api/logs/retention", "/api/configuration/operations/" + id,
                request, cancellationToken: UiCancellation).ConfigureAwait(true);
            ClusterOperationalSaveResult? result = response.Deserialize<ClusterOperationalSaveResult>(ContractJson.Options);
            message = Ui.Deferred("Saved {0} / shared {1} / snapshot {2} / applied {3}", result?.Saved.Outcome, result?.Shared.Outcome, result?.Snapshot.Outcome, result?.Activation.Outcome);
            shared = await Api.GetAsync<LogRetentionView>("/api/logs/retention", cancellationToken: UiCancellation).ConfigureAwait(true);
            observed = shared;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (exception is HttpRequestException)
            {
                message = Ui.Deferred("The retention API result is unconfirmed.");
            }
            else
            {
                message = Ui.DeferredException(exception);
            }
        }
    }

    /// <summary>編集先を切り替えず、指定したPeerの反映状態だけを照会します。</summary>
    /// <returns>現在選択している照会先の表示完了です。</returns>
    private async Task InspectPeerAsync(ChangeEventArgs args, CancellationToken cancellationToken = default)
    {
        string? requested = args.Value?.ToString();
        statusPeer = requested;
        observed = null;
        try
        {
            LogRetentionView? result = await Api.GetForPeerAsync<LogRetentionView>("/api/logs/retention", requested, cluster, cancellationToken).ConfigureAwait(true);
            (HistoryArchiveStatus? Status, HistoryCheckpointInfo[] Points) history = await ReadHistoryAsync(requested, cluster, cancellationToken).ConfigureAwait(true);
            if (string.Equals(statusPeer, requested, StringComparison.Ordinal))
            {
                observed = result;
                historyStatus = history.Status;
                checkpoints.Clear();
                checkpoints.AddRange(history.Points);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = Ui.Deferred("The peer retention state could not be confirmed.");
        }
    }

    /// <summary>更新途中で保守情報が未対応でも、確認できた保持設定を表示します。</summary>
    /// <returns>同じPeerから取得した保守情報、未対応なら空です。</returns>
    private async Task<(HistoryArchiveStatus? Status, HistoryCheckpointInfo[] Points)> ReadHistoryAsync(string? peerId, string? clusterId, CancellationToken cancellationToken = default)
    {
        try
        {
            HistoryArchiveStatus? status = await Api.GetForPeerAsync<HistoryArchiveStatus>("/api/history/status", peerId, clusterId, cancellationToken).ConfigureAwait(true);
            HistoryCheckpointInfo[]? points = await Api.GetForPeerAsync<HistoryCheckpointInfo[]>("/api/history/checkpoints", peerId, clusterId, cancellationToken).ConfigureAwait(true);
            return (status, points ?? []);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return (null, []);
        }
    }

    /// <summary>保持対象ごとの編集値を一時保持します。</summary>
    private sealed class RetentionRow
    {
        /// <summary>保持する情報の種類です。</summary>
        public ERetentionCategory Category
        {
            get; init;
        }

        /// <summary>共通期間をこの種類だけ上書きするかです。</summary>
        public bool Override
        {
            get; set;
        }

        /// <summary>保持方針を編集するログレベルです。</summary>
        public string Level
        {
            get;
            init;
        } = string.Empty;

        /// <summary>詳細データを保持する日数です。</summary>
        public string RawDays
        {
            get;
            set;
        } = string.Empty;

        /// <summary>圧縮済みの履歴も含めた保持日数です。</summary>
        public string TotalDays
        {
            get;
            set;
        } = string.Empty;

        /// <summary>保持期間を無期限にするかどうかです。</summary>
        public bool Unlimited
        {
            get;
            set;
        }

        /// <summary>編集中の行を識別する短い表示です。</summary>
        /// <returns>行の識別に必要な表示値です。</returns>
        public override string ToString() => Level;
    }
}
