using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Updates;

/// <summary>配布マニフェストと固定ピア集合から更新操作を要求します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class UpdatePanel
{
    private static string MatchManifest => "manifest";
    private static string MatchVariants => "variants";

    [Inject]
    private ManagementApi Api
    {
        get; set;
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

    private bool open, reading, allowActive, remainingConfirmed;
    private string payloadRoot = string.Empty;
    private Func<string>? message = () => string.Empty;
    private ReleaseManifest? manifest;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<ReleaseVariant>? variants;
    private readonly HashSet<string> peers = new(StringComparer.Ordinal);
    private EUpdateFailurePolicy failure = EUpdateFailurePolicy.RollbackSuccessful;
    private EUpdateRollbackFailurePolicy rollback = EUpdateRollbackFailurePolicy.ExcludeFailed;
    private string timeoutSeconds = "600";
    private bool scheduled;
    private string scheduleAt = string.Empty, maximumDelay = "300";
    private UpdateScheduleView? editing;
    private string? ownerPeer, ownerCluster;

    /// <summary>現在の設定を使って更新の入力画面を開きます。</summary>
    private void Open()
    {
        editing = null;
        ownerPeer = Api.SelectedPeerId ?? Connection.Current?.PeerId;
        ownerCluster = Api.SelectedClusterId;
        scheduled = false;
        scheduleAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        peers.Clear();
        foreach (Contracts.Cluster.PeerInfo? peer in Connection.Current?.Peers ?? [])
        {
            if (peer.Status == ReArtWord.Contents.BackgroundService.Contracts.Cluster.EPeerStatus.Ready)
            {
                peers.Add(peer.PeerId);
            }
        }

        open = true;
    }

    /// <summary>確認した未開始予約を、同じ更新IDと保存版で変更します。</summary>
    private void EditSchedule(UpdateScheduleView view)
    {
        editing = view;
        scheduled = true;
        ownerPeer = view.Schedule.OwnerPeerId;
        ownerCluster = Api.SelectedClusterId;
        UpdateSchedulePlan plan = view.Schedule.Plan;
        ClusterUpdateRequest request = plan.Update;
        manifest = request.Manifest;
        variants = request.Variants;
        payloadRoot = request.PayloadRoot;
        peers.Clear();
        peers.UnionWith(request.PeerIds);
        failure = request.FailurePolicy;
        rollback = request.RollbackFailurePolicy;
        allowActive = request.AllowWhileActive;
        remainingConfirmed = request.RemainingOperationConfirmed;
        timeoutSeconds = (request.PeerTimeoutMilliseconds / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        maximumDelay = plan.MaximumDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        scheduleAt = plan.StartAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        open = true;
    }

    /// <summary>更新対象のピア集合を明示的に変更します。</summary>
    private void SelectPeer(string id, bool selected)
    {
        if (selected)
        {
            peers.Add(id);
        }
        else
        {
            peers.Remove(id);
        }
    }

    /// <summary>選択した配布マニフェストを読んで更新入力へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "入力streamはstreamLifetimeのawait usingで確実に解放します。")]
    private async Task ReadManifestAsync(InputFileChangeEventArgs e)
    {
        reading = true;
        manifest = null;
        try
        {
            Stream stream = e.File.OpenReadStream(16777216, cancellationToken: UiCancellation);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable streamLifetime = stream.ConfigureAwait(true);
            using JsonDocument? json = await JsonDocument.ParseAsync(stream, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (json.RootElement.TryGetProperty(MatchManifest, out JsonElement value))
            {
                manifest = value.Deserialize<ReleaseManifest>(ContractJson.Options);
                variants = json.RootElement.TryGetProperty(MatchVariants, out JsonElement alternatives) ? alternatives.Deserialize<List<ReleaseVariant>>(ContractJson.Options)
                                                                                                        : null;
            }
            else
            {
                manifest = json.RootElement.Deserialize<ReleaseManifest>(ContractJson.Options);
                variants = null;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            message = Ui.Deferred("The release manifest could not be read.");
        }
        finally
        {
            reading = false;
        }
    }

    /// <summary>選択した対象の開始操作を記録して要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task StartAsync()
    {
        Guid? operation = null;
        try
        {
            if (manifest is null)
            {
                throw UiError.InvalidOperation("Load a release manifest.");
            }

            if (string.IsNullOrWhiteSpace(payloadRoot))
            {
                throw UiError.InvalidOperation("Enter an absolute payload path.");
            }

            if (peers.Count == 0)
            {
                throw UiError.InvalidOperation("Select at least one peer to update.");
            }

            int seconds = FormInput.Integer(timeoutSeconds, "Confirmation timeout per peer (seconds)", 1, int.MaxValue / 1000);
            ClusterUpdateRequest? request = new ClusterUpdateRequest(editing?.Schedule.Plan.Update.OperationId ?? Guid.NewGuid(), manifest, payloadRoot, peers.ToArray(), failure, rollback, allowActive, remainingConfirmed, seconds * 1000, variants);
            if (scheduled)
            {
                int delay = FormInput.Integer(maximumDelay, "Maximum start delay (seconds)", 0, 604800);
                string utc = "Z";
                bool explicitOffset = scheduleAt.EndsWith(utc, StringComparison.OrdinalIgnoreCase) || (6 <= scheduleAt.Length && scheduleAt[^6] is '+' or '-' && scheduleAt[^3] == ':');
                if (!explicitOffset || !DateTimeOffset.TryParse(scheduleAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset when))
                {
                    throw UiError.InvalidOperation("Enter an ISO date and time with Z or an explicit UTC offset.");
                }
                UpdateScheduleRequest reservation = new(Guid.NewGuid(), new(when, delay, request), editing?.Version);
                operation = reservation.OperationId;
                await Journal.StartAsync(reservation.OperationId, "Update schedule", "/api/updates/schedules/save", "/api/updates/schedules/operations/" + reservation.OperationId,
                    reservation, targetPeer: ownerPeer, targetCluster: ownerCluster, cancellationToken: UiCancellation).ConfigureAwait(true);
                open = false;
                message = Ui.Deferred("Operation {0}: {1}", reservation.OperationId, OperationJournal.Outcome(Journal.LastResult));
                return;
            }
            operation = request.OperationId;
            open = false;
            await Journal.StartAsync(request.OperationId, "Release update", "/api/updates/start", "/api/updates/" + request.OperationId, request, targetPeer: ownerPeer, targetCluster: ownerCluster, cancellationToken: UiCancellation).ConfigureAwait(true);
            message = Ui.Deferred("Operation {0}: {1}", request.OperationId, OperationJournal.Outcome(Journal.LastResult));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JSException or OverflowException)
        {
            message = operation is null ? Ui.DeferredException(exception)
                                        : Ui.Deferred("Update is unconfirmed. Query operation {0}.", operation);
        }
    }
}
