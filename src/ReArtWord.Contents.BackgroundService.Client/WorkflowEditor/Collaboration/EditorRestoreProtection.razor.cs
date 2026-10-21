using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>復元対象の編集保護を追跡して子画面へ通知します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class EditorRestoreProtection : IAsyncDisposable
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    /// <summary>保護範囲の変更を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback Changed
    {
        get;
        set;
    }

    /// <summary>画面が共有設定の編集参加者であるかどうかです。</summary>
    [Parameter]
    public bool IsConfigurationParticipant
    {
        get;
        set;
    }

    /// <summary>確認または編集するワークフローの識別子です。</summary>
    [Parameter]
    public string? WorkflowId
    {
        get;
        set;
    }

    /// <summary>復元保護による読み取り専用状態の変更を通知します。</summary>
    [Parameter]
    public EventCallback<bool> ReadOnlyChanged
    {
        get;
        set;
    }

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private SnapshotEditLock[] visible = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private Func<string>? problem;
    private string? observedLocks;

    /// <summary>初回表示で必要な状態と継続監視を準備します。</summary>
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override void OnInitialized() => polling = PollAsync();

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer? timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    List<SnapshotEditLock>? locks = await Api.GetAsync<List<SnapshotEditLock>>("/api/snapshots/locks", lifetime.Token).ConfigureAwait(true);
                    if (locks is null)
                    {
                        throw new InvalidOperationException();
                    }

                    string revision = Contracts.Serialization.ContractJson.Hash(locks);
                    bool changed = problem is not null || !string.Equals(observedLocks, revision, StringComparison.Ordinal);
                    Model.Locks.Update(locks);
                    visible = locks.Where(item => item.IsConfigurationParticipant == IsConfigurationParticipant && string.Equals(item.WorkflowId, (WorkflowId ?? Model.Draft.Document.Id), StringComparison.Ordinal)).ToArray();
                    await ReadOnlyChanged.InvokeAsync(visible.Length != 0).ConfigureAwait(true);
                    problem = null;
                    observedLocks = revision;
                    if (changed)
                    {
                        await Changed.InvokeAsync().ConfigureAwait(true);
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
                {
                    problem = Ui.Deferred("Restore protection could not be refreshed. Previously confirmed protection remains active.");
                }

                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>画面が所有する継続処理を終了し、保持する参照と資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await polling.ConfigureAwait(true);
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
