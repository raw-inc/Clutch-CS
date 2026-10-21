using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>共有状態の競合を比較し、確認した候補の解消を要求します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class ConflictPanel
{
    private static string MatchClusterOperational => "cluster.operational";
    private static string MatchClusterOperationalActivation => "cluster.operational.activation";

    [Inject]
    private ManagementApi Api
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

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<ConflictListSummary> conflicts = [];
    private string? cursor;
    private StateConflict? selected;
    private string? version, cluster, peer;
    private Func<string>? message;
    private string edited = string.Empty;
    private bool loaded, busy, reviewed, edit;
    private Guid? lastId;
    private ConflictResolveResult? result;
    private bool SameConnection => string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(peer, Api.SelectedPeerId, StringComparison.Ordinal);
    private StateConflictCandidate? SelectedCandidate => selected?.Candidates.FirstOrDefault(candidate => string.Equals(candidate.Version, version, StringComparison.Ordinal));

    /// <summary>表示対象の一覧または状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task LoadAsync() => LoadPageAsync(false);

    /// <summary>競合一覧の指定ページを取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadPageAsync(bool next)
    {
        busy = true;
        message = null;
        (string? SelectedClusterId, string? SelectedPeerId) source = (Api.SelectedClusterId, Api.SelectedPeerId);
        try
        {
            ConflictListPage? page = await Api.PostAsync<ConflictListPage>("/api/conflicts/query", new ConflictListQuery(Cursor: next ? cursor
                                                                                                                                      : null), cancellationToken: UiCancellation).ConfigureAwait(true);
            if (source != (Api.SelectedClusterId, Api.SelectedPeerId))
            {
                return;
            }

            conflicts = page?.Entries ?? throw UiError.InvalidOperation("The candidate list is unconfirmed.");
            cursor = page.Cursor;
            (cluster, peer) = source;
            loaded = true;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("Current candidates could not be retrieved. Previously displayed candidates are not treated as resolved.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>対象の現在値を取得して編集または選択画面を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenAsync(ConflictListSummary summary)
    {
        busy = true;
        message = null;
        try
        {
            if (!SameConnection || !summary.DetailAvailable)
            {
                throw UiError.InvalidOperation("Comparison content has not been retrieved.");
            }

            (string? cluster, string? peer) source = (cluster, peer);
            ConflictDetail? detail = await Api.PostAsync<ConflictDetail>("/api/conflicts/detail", new ConflictDetailRequest(summary.Id), cancellationToken: UiCancellation).ConfigureAwait(true);
            if (source != (Api.SelectedClusterId, Api.SelectedPeerId))
            {
                return;
            }

            if (detail?.Conflict is not { } conflict)
            {
                message = (detail?.DetailReason) is null ? Ui.Deferred("Comparison content is unavailable. The conflict remains unresolved.")
                                                         : Ui.Deferred("{0}", detail?.DetailReason);
                return;
            }

            if (!string.Equals(conflict.Id, summary.Id, StringComparison.Ordinal) || !conflict.Candidates.Select(item => item.Version).Order(StringComparer.Ordinal).SequenceEqual(summary.CandidateVersions.Order(StringComparer.Ordinal)))
            {
                throw UiError.InvalidOperation("The candidate set changed. Retrieve the list from the first page.");
            }

            selected = conflict;
            version = null;
            reviewed = false;
            edit = false;
            edited = string.Empty;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = Ui.Deferred("This candidate cannot be compared. If candidates changed or expired, retrieve the first page again.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>指定した候補を現在の選択へ反映します。</summary>
    private void Choose(string candidateVersion)
    {
        version = candidateVersion;
        edit = false;
        edited = string.Empty;
        message = null;
    }

    /// <summary>比較画面で編集した候補値を保持します。</summary>
    private void ChangeEdit(ChangeEventArgs args)
    {
        try
        {
            if ((bool)args.Value!)
            {
                string raw = EditableContent(selected!, SelectedCandidate!).GetRawText();
                if (262144 < raw.Length)
                {
                    throw UiError.InvalidOperation("The comparison editor limit was exceeded.");
                }

                edited = raw;
            }

            edit = (bool)args.Value!;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            edit = false;
            message = Ui.Deferred("This candidate cannot be edited for comparison. Select an existing revision.");
        }
    }

    /// <summary>確認中の競合解消要求を破棄して一覧へ戻ります。</summary>
    private void Cancel()
    {
        if (!busy)
        {
            selected = null;
        }
    }

    /// <summary>確認した競合または実体の解消方法を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ResolveAsync()
    {
        ConflictResolveRequest request;
        try
        {
            if (!SameConnection)
            {
                throw UiError.InvalidOperation("The connection changed.");
            }

            request = BuildRequest(selected!, version, reviewed, edit, edited);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            message = Ui.Deferred("Nothing was sent. Check the candidate selection, revision confirmations, and JSON type and size.");
            return;
        }

        busy = true;
        lastId = request.OperationId;
        result = null;
        message = null;
        try
        {
            JsonElement response = await Journal.StartAsync(request.OperationId, "conflict", "/api/conflicts/resolve", "/api/conflicts/operations/" + request.OperationId, request, cancellationToken: UiCancellation).ConfigureAwait(true);
            result = response.Deserialize<ConflictResolveResult>(ContractJson.Options);
            selected = null;
            message = Ui.Deferred("Check saved, shared and locally applied results separately. You can refresh the list.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or JSException)
        {
            message = Ui.Deferred("The result is unconfirmed. The original request and ID are retained. Query that result instead of submitting a new resolution.");
            selected = null;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>選択したデータまたは元の操作結果を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadAsync()
    {
        busy = true;
        message = null;
        try
        {
            OperationRecord? item = Journal.Items.Single(entry => entry.OperationId == lastId);
            await Journal.ReadAsync(item, cancellationToken: UiCancellation).ConfigureAwait(true);
            result = Journal.Items.Single(entry => entry.OperationId == lastId).Result?.Deserialize<ConflictResolveResult>(ContractJson.Options);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or JSException)
        {
            message = Ui.Deferred("The original result is still unconfirmed. Nothing was executed again.");
        }
        finally
        {
            busy = false;
        }
    }
}
