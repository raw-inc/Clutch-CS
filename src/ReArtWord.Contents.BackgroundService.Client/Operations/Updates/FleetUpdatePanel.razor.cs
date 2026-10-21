using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Updates;

/// <summary>全クラスタの固定計画と、ClientHostが所有する更新進捗を表示します。</summary>
// 抑制理由: Razorの公開partial型と固定イベント署名を実装します。I/Oへ画面寿命の取消しを渡します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorの公開partial型です。"),
 System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Razorイベントの固定署名からUiCancellationを渡します。")]
public sealed partial class FleetUpdatePanel
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    private ReleaseManifest? manifest;
    private ReleaseVariant? clientRelease;

    // 抑制理由: 開始済み要求が保持する旧配列を変更せず、選択した配布定義の参照を交換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "開始済み要求の配列は変更せず参照を交換します。")]
    private ReleaseVariant[]? variants;
    private FleetUpdateRequest? plan;

    /// <summary>Dashboardと共通の更新一覧です。</summary>
    // 抑制理由: 親が取得したスナップショットをParameterとして受け取り、内容を変更しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "RazorのParameterとして親のスナップショットを受け取ります。")]
    [Parameter] public IReadOnlyList<FleetUpdateView> Updates
    {
        get; set;
    } = [];

    /// <summary>共通の照会が失敗しているかです。</summary>
    [Parameter] public bool ProgressUnavailable
    {
        get; set;
    }

    /// <summary>操作直後に同じ進捗一覧を再取得します。</summary>
    [Parameter] public EventCallback RefreshProgress
    {
        get; set;
    }

    private bool busy, allowActive;
    private int maximumAttempts = 3;
    private int maximumParallelism = 2;
    private string payloadRoot = string.Empty, timeoutSeconds = "600";
    private Func<string>? message;

    /// <summary>編集後に古い入力の計画を開始しないよう、プレビューをやり直します。</summary>
    private void InvalidatePlan() => plan = null;

    /// <summary>単一RIDと既存bundleのマニフェストを読み、以前の計画を破棄します。</summary>
    /// <returns>選択した配布定義の読取りです。</returns>
    // 抑制理由: streamLifetimeで入力streamを非同期解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "入力streamはawait usingで所有します。")]
    private async Task ReadManifestAsync(InputFileChangeEventArgs args)
    {
        manifest = null;
        clientRelease = null;
        variants = null;
        plan = null;
        await GuardAsync(async () =>
        {
            Stream stream = args.File.OpenReadStream(2097152, UiCancellation);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = stream.ConfigureAwait(true);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: UiCancellation).ConfigureAwait(true);
            string manifestProperty = "manifest", variantsProperty = "variants";
            if (document.RootElement.TryGetProperty(manifestProperty, out JsonElement primary))
            {
                manifest = primary.Deserialize<ReleaseManifest>(ContractJson.Options);
                string clientProperty = "client";
                if (document.RootElement.TryGetProperty(clientProperty, out JsonElement client))
                {
                    clientRelease = client.Deserialize<ReleaseVariant>(ContractJson.Options);
                }
                if (document.RootElement.TryGetProperty(variantsProperty, out JsonElement alternatives))
                {
                    variants = alternatives.Deserialize<ReleaseVariant[]>(ContractJson.Options);
                }
            }
            else
            {
                manifest = document.RootElement.Deserialize<ReleaseManifest>(ContractJson.Options);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>現在発見中の全対象と入力方針をHost側で固定します。</summary>
    /// <returns>開始前に表示する計画です。</returns>
    private Task PreviewAsync() => GuardAsync(async () =>
    {
        plan = null;
        if (manifest is null)
        {
            throw UiError.InvalidOperation("Load a release manifest.");
        }
        int seconds = FormInput.Integer(timeoutSeconds, "Confirmation timeout per peer (seconds)", 1, 3600);
        FleetReleaseSelection selection = new(manifest, payloadRoot, allowActive, maximumAttempts, seconds * 1000, variants, clientRelease, maximumParallelism);
        plan = await Api.PostAsync<FleetUpdateRequest>("/client/updates/preview", selection, UiCancellation).ConfigureAwait(true);
    });

    /// <summary>プレビュー済みの同じIDだけで受付を要求します。応答欠落後もIDを保持します。</summary>
    /// <returns>受付または元操作の保存結果です。</returns>
    private Task StartAsync() => GuardAsync(async () =>
    {
        if (plan is null)
        {
            return;
        }
        await Api.PostAsync<FleetUpdateStatus>("/client/updates", plan, UiCancellation).ConfigureAwait(true);
        plan = null;
        await RefreshProgress.InvokeAsync().ConfigureAwait(true);
    });

    /// <summary>元Updaterの結果を照会し、更新要求を再送しません。</summary>
    /// <returns>再確認した元操作の結果です。</returns>
    private Task ReconcileAsync(Guid id) => GuardAsync(async () =>
    {
        await Api.PostAsync<FleetUpdateStatus>("/client/updates/" + id.ToString("D") + "/reconcile", new { }, UiCancellation).ConfigureAwait(true);
        await RefreshProgress.InvokeAsync().ConfigureAwait(true);
    });

    /// <summary>入力中の多重要求を防ぎ、例外を画面上に残します。</summary>
    /// <returns>一つの画面操作です。</returns>
    private async Task GuardAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        message = null;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            message = Ui.Deferred("The release update could not be confirmed. Check the payload path, peer state and saved operation before trying again.");
        }
        finally
        {
            busy = false;
        }
    }

}
