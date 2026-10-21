using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Persistence;

/// <summary>内部データの取得・ローカル編集・照合と記録の出力を提供します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class InternalDataPanel
{
    [Inject]
    private ManagementApi Api
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

    [Inject]
    private ConnectionState Connection
    {
        get;
        set;
    } = null!;

    private string workflow = string.Empty, key = string.Empty, raw = string.Empty;
    private EInternalDataScope scope;
    private InternalDataClientView? view;
    private InternalDataBrowserDraft? retained;
    private Guid? command;
    private JsonElement? query;
    private Func<string>? message;
    private bool busy, rawDirty, discardOpen, writeUnknown;
    private InternalDataTarget Target => new(scope == EInternalDataScope.Cluster ? string.Empty
                                                                                 : workflow, scope, key);
    private bool Matches => view is not null && view.Target == Target && string.Equals(view.ClusterId, Api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(view.PeerId, Api.SelectedPeerId, StringComparison.Ordinal);
    private bool Editable => Matches && view!.Current.State.CanRead && view.Current.Value?.Kind != EValueKind.Binary && view.Current.State.CanWrite && view.Policy.Offline == EAvailabilityOffline.LocalUnconfirmed;
    private string CacheKey => "contents.internal-data.browser." + JsonSerializer.Serialize(new[] { Api.SelectedClusterId, Api.SelectedPeerId, workflow, scope.ToString(), key });

    /// <summary>選択したデータまたは元の操作結果を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ReadAsync(bool newInstance)
    {
        busy = true;
        message = null;
        (string? SelectedClusterId, string? SelectedPeerId, InternalDataTarget Target) owner = (Api.SelectedClusterId, Api.SelectedPeerId, Target);
        try
        {
            retained = newInstance ? null
                                   : await Storage.ReadAsync<InternalDataBrowserDraft>(CacheKey, cancellationToken: UiCancellation).ConfigureAwait(true);
            Guid? instance = retained?.Baseline.InstanceId ?? Connection.Current?.InstanceId;
            InternalDataClientView? result = await Api.PostAsync<InternalDataClientView>("/client/internal-data/read", new InternalDataClientTarget(Target, instance), cancellationToken: UiCancellation).ConfigureAwait(true);
            if (owner != (Api.SelectedClusterId, Api.SelectedPeerId, Target))
            {
                return;
            }

            view = result ?? throw UiError.InvalidOperation("The internal value is unconfirmed.");
            command = view.PendingCommandId ?? retained?.PendingCommandId;
            writeUnknown = retained?.WriteUnknown == true || string.Equals(view.Current.State.Reason, "Availability.OriginalWriteUnknown", StringComparison.Ordinal);
            if (retained?.Raw is not null)
            {
                raw = retained.Raw;
                rawDirty = true;
            }
            else
            {
                raw = JsonText(view.Current.Value);
                rawDirty = false;
            }

            await PersistAsync().ConfigureAwait(true);
        }
        catch (HttpRequestException)
        {
            if (owner != (Api.SelectedClusterId, Api.SelectedPeerId, Target))
            {
                return;
            }

            InternalDataClientView? cached = retained?.Baseline;
            if (cached is null || cached.Policy.Offline == EAvailabilityOffline.Unavailable)
            {
                view = null;
                message = Ui.Deferred("ClientHost is unreachable and no confirmed value is available for this purpose.");
            }
            else
            {
                bool writable = cached.Policy.Offline == EAvailabilityOffline.LocalUnconfirmed && !retained!.WriteUnknown;
                view = cached with
                {
                    Online = false,
                    Current = cached.Current with
                    {
                        State = cached.Current.State with
                        {
                            IsStale = true,
                            CanWrite = writable,
                            Reason = "ClientHost.OfflineBrowserDraft"
                        }
                    }
                };
                raw = retained!.Raw ?? JsonText(cached.Current.Value);
                rawDirty = retained.Raw is not null;
                command = retained.PendingCommandId;
                writeUnknown = retained.WriteUnknown;
                message = Ui.Deferred("ClientHost is also offline. Input is retained only as a browser draft; it is not applied to Core or the local effective value.");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or JSException or OperationCanceledException)
        {
            message = Ui.Deferred("The target, type or last observed instance is unconfirmed. No value was substituted automatically.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>選択した内部データをローカル編集用の草稿へ読み込みます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task EditAsync(ChangeEventArgs args)
    {
        raw = args.Value?.ToString() ?? string.Empty;
        rawDirty = true;
        await PersistAsync().ConfigureAwait(true);
    }

    /// <summary>検証した内部データの変更を送信前の草稿として保持します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task StageAsync()
    {
        await RunAsync(async () =>
        {
            if (!Editable || view is null || writeUnknown)
            {
                throw UiError.InvalidOperation("The current policy does not allow changes.");
            }

            if (262144 < raw.Length)
            {
                throw UiError.InvalidOperation("Limit input to 256 Ki characters.");
            }

            using JsonDocument? json = JsonDocument.Parse(raw);
            string? baseline = retained?.Baseline.Current.Revision ?? view.Current.Revision;
            if (view.Current.State.IsLocalUnconfirmed)
            {
                baseline = view.Current.Revision;
            }

            InternalDataClientChange? change = new InternalDataClientChange(new(Target, view.InstanceId), baseline, NodeValue.FromJson(json.RootElement), Guid.NewGuid());
            view = await Api.PostAsync<InternalDataClientView>("/client/internal-data/stage", change, cancellationToken: UiCancellation).ConfigureAwait(true) ?? throw UiError.InvalidOperation("The candidate is unconfirmed.");
            message = Ui.Deferred("{0}", view.Current.State.Reason);
            await PersistAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>基準版とローカル草稿を照合して反映結果を確認します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task ReconcileAsync() => RunAsync(async () =>
    {
        if (!Matches || view is null || writeUnknown)
        {
            throw UiError.InvalidOperation("Check the original target and write.");
        }

        view = await Api.PostAsync<InternalDataClientView>("/client/internal-data/reconcile", new InternalDataClientTarget(Target, view.InstanceId), cancellationToken: UiCancellation).ConfigureAwait(true) ?? throw UiError.InvalidOperation("Integration is unconfirmed.");
        command = view.PendingCommandId;
        writeUnknown = string.Equals(view.Current.State.Reason, "Availability.OriginalWriteUnknown", StringComparison.Ordinal);
        if (!view.Current.State.IsLocalUnconfirmed)
        {
            raw = JsonText(view.Current.Value);
            rawDirty = false;
            retained = null;
        }

        message = (view.Current.State.Reason) is null ? Ui.Deferred("Compare-and-save was confirmed.")
                                                      : Ui.Deferred("{0}", view.Current.State.Reason);
        await PersistAsync().ConfigureAwait(true);
    });

    /// <summary>選択したローカル草稿を破棄して保存状態を更新します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task DiscardAsync() => RunAsync(async () =>
    {
        discardOpen = false;
        if (!Matches || view is null || writeUnknown)
        {
            throw UiError.InvalidOperation("A request with an unknown result cannot be discarded.");
        }

        view = await Api.PostAsync<InternalDataClientView>("/client/internal-data/discard", new InternalDataClientTarget(Target, view.InstanceId), cancellationToken: UiCancellation).ConfigureAwait(true) ?? throw UiError.InvalidOperation("Discard is unconfirmed.");
        raw = JsonText(view.Current.Value);
        rawDirty = false;
        retained = null;
        command = null;
        message = Ui.Deferred("The saved value was confirmed and the local candidate discarded.");
        await PersistAsync().ConfigureAwait(true);
    });

    /// <summary>確認用のJSON記録をブラウザーから出力します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task DownloadAsync() => Storage.DownloadAsync("internal-data-draft.json", new { view?.ClusterId, view?.PeerId, view?.InstanceId, Target, Raw = raw, BaseRevision = retained?.Baseline.Current.Revision }, cancellationToken: UiCancellation).AsTask();

    /// <summary>独立した内部データ編集用のローカル識別子を作ります。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task NewInstanceAsync()
    {
        await DownloadAsync().ConfigureAwait(true);
        await ReadAsync(true).ConfigureAwait(true);
    }

    /// <summary>現在の検索範囲に対応する内部データを照会します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task QueryAsync() => RunAsync(async () =>
    {
        if (command is null || view is null || !Matches)
        {
            return;
        }

        view = await Api.PostAsync<InternalDataClientView>("/client/internal-data/query", new InternalDataClientTarget(Target, view.InstanceId), cancellationToken: UiCancellation).ConfigureAwait(true) ?? throw UiError.InvalidOperation("The original result remains unconfirmed.");
        writeUnknown = string.Equals(view.Current.State.Reason, "Availability.OriginalWriteUnknown", StringComparison.Ordinal);
        if (!view.Current.State.IsLocalUnconfirmed)
        {
            raw = JsonText(view.Current.Value);
            rawDirty = false;
            retained = null;
        }

        query = JsonSerializer.SerializeToElement(view.Current.State, ContractJson.Options);
        await PersistAsync().ConfigureAwait(true);
        message = writeUnknown ? Ui.Deferred("The original write is still unconfirmed. It was not resent.")
                               : Ui.Deferred("The original fixed result was applied. Confirmed success updates saved state; a confirmed non-execution allows comparing the retained candidate again. No new write was sent.");
    });

    /// <summary>ローカルの編集状態をブラウザー保存領域へ保存します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PersistAsync()
    {
        if (view is null || !Matches || 262144 < raw.Length)
        {
            return;
        }

        retained = InternalDataBrowserDraft.Capture(view, retained, raw, rawDirty, command, writeUnknown);
        try
        {
            await Storage.WriteAsync(CacheKey, retained, cancellationToken: UiCancellation).ConfigureAwait(true);
        }
        catch (JSException)
        {
            message = Ui.Deferred("The browser draft could not be saved. Copy the input before closing this page.");
        }
    }

    /// <summary>指定した処理を実行し、結果または確認不能状態を画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        message = null;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException or JSException)
        {
            message = Ui.Deferred("Unconfirmed. Input is retained. Check JSON, the base revision, instance and original write result.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>内部データを編集・確認用のJSON文字列へ変換します。</summary>
    /// <returns>JSON型の実値、JSONでなければ空文字列です。</returns>
    private static string JsonText(NodeValue? value) => value?.Kind == EValueKind.Json ? value.Json.GetRawText()
                                                                                       : value is null ? "null"
                                                                                                       : "(Binary values cannot be edited in this JSON field)";

    /// <summary>比較する内容を表示用の文字列へ変換します。</summary>
    /// <returns>型と欠損状態を区別した比較用の表示です。</returns>
    private static string Preview(NodeValue? value)
    {
        string text = JsonText(value);
        return text.Length <= 4096 ? text
                                   : text[..4096] + "…display truncated";
    }
}
