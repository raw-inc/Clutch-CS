using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Journal;

/// <summary>元の操作IDを保持した結果照会と操作記録の出力を提供します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class OperationList
{
    private static string MatchDetailReason => "detailReason";
    private static string MatchDetailsUnavailable => "detailsUnavailable";
    private static string MatchPeers => "peers";
    private static string MatchTargets => "targets";
    private static string MatchTarget => "target";
    private static string MatchApply => "apply";
    private static string MatchCompensation => "compensation";

    [Inject]
    private OperationJournal Journal
    {
        get;
        set;
    } = null!;

    [Inject]
    private BrowserStorage Storage
    {
        get;
        set;
    } = null!;

    private Func<string>? problem;
    private string kind = "restore";
    private string identity = string.Empty;

    /// <summary>実行結果を確認できた行だけに、元の実体への取消操作を表示します。</summary>
    /// <returns>一件の実行状態。別種の結果や古い形式ならnull。</returns>
    private static WorkflowRunStatus? ReadRun(OperationRecord item)
    {
        string prefix = "/api/runs/";
        string workflow = "workflowId";
        if (!item.ReadPath.StartsWith(prefix, StringComparison.Ordinal) || item.Result is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(workflow, out _))
        {
            return null;
        }
        try
        {
            return value.Deserialize<WorkflowRunStatus>(ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>元の操作結果からピア・対象ごとの進捗を列挙します。</summary>
    /// <returns>結果に記録された対象ごとの進捗表示です。</returns>
    private IEnumerable<string> Progress(JsonElement? result)
    {
        if (result is not JsonElement value || value.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (value.TryGetProperty(MatchDetailReason, out JsonElement omitted) && omitted.ValueKind == JsonValueKind.String)
        {
            yield return Ui["Execution detail omitted: "] + omitted.GetString();
        }

        if (value.TryGetProperty(MatchDetailsUnavailable, out JsonElement unavailable) && unavailable.ValueKind == JsonValueKind.True)
        {
            yield return Ui["Full execution output is unavailable on this peer. The confirmed outcome and summary are shown."];
        }

        if (value.TryGetProperty(MatchPeers, out JsonElement peers) && peers.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement peer in peers.EnumerateArray())
            {
                yield return $"{Text(peer, "peerId")}: {Ui[Text(peer, "phase")]} / {Ui[Text(peer, "outcome")]}";
            }
        }

        if (value.TryGetProperty(MatchTargets, out JsonElement targets) && targets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement target in targets.EnumerateArray())
            {
                if (target.TryGetProperty(MatchTarget, out JsonElement identity))
                {
                    string applied = target.TryGetProperty(MatchApply, out JsonElement apply) ? Text(apply, "outcome")
                                                                                              : Text(target, "acceptance");
                    string compensation = target.TryGetProperty(MatchCompensation, out JsonElement restore) ? Text(restore, "outcome")
                                                                                                            : "Not applicable";
                    yield return $"{Text(identity, "peerId")}/{Text(identity, "id")}: " + Ui.Format("Apply / accept {0} · compensate {1}", Ui[applied], Ui[compensation]);
                }
            }
        }
    }

    /// <summary>表示対象の値を文字列へ変換します。</summary>
    /// <returns>JSON値の入力用文字列、未定義の場合は空文字列です。</returns>
    private static string Text(JsonElement value, string property)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement part) ? part.ToString()
                                                                                                               : "Unconfirmed";
    }

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override async Task OnInitializedAsync()
    {
        Journal.Changed += Changed;
        await RunAsync(() => Journal.LoadAsync(cancellationToken: UiCancellation)).ConfigureAwait(true);
    }

    /// <summary>操作記録の変更を一覧の再描画へ通知します。</summary>
    private void Changed()
    {
        RequestRenderFromEvent();
    }

    /// <summary>画面が登録した通知購読を解除します。</summary>
    /// <returns>購読解除と取消資源の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            Journal.Changed -= Changed;
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>入力された元の操作IDから保存済み結果を照会します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task LookupAsync() => RunAsync(() => Journal.TrackAsync(kind, Guid.Parse(identity), cancellationToken: UiCancellation));

    /// <summary>選択した元の操作IDの結果を再照会します。</summary>
    /// <returns>照会と画面反映の完了です。</returns>
    // 抑制理由: 行ボタンのUIコールバックとして、所有トークンを照会へ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "行ボタンのUIコールバックで、照会にはUiCancellationを明示します。")]
    private Task ReadResultAsync(OperationRecord item) => RunAsync(() => Journal.ReadAsync(item, UiCancellation));

    /// <summary>開始済みの復元を元の操作IDで復旧させ、結果を記録します。</summary>
    /// <returns>復旧要求と現時点の結果反映の完了です。</returns>
    // 抑制理由: 行ボタンのUIコールバックとして、所有トークンを復旧要求へ渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "行ボタンのUIコールバックで、復旧要求にはUiCancellationを明示します。")]
    private Task RecoverResultAsync(OperationRecord item) => RunAsync(() => Journal.RecoverAsync(item, UiCancellation));

    /// <summary>確認用のJSON記録をブラウザーから出力します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task DownloadAsync(OperationRecord item) => RunAsync(async () => await Storage.DownloadAsync(item.OperationId + ".json", item.Request, cancellationToken: UiCancellation).ConfigureAwait(true));

    /// <summary>指定した処理を実行し、結果または確認不能状態を画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
            problem = null;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JSException or OperationCanceledException or FormatException)
        {
            problem = Ui.Deferred("The result is unconfirmed. The original ID and request are retained.");
        }
    }
}
