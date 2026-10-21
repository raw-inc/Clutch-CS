using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Scheduling;

/// <summary>予約の登録・比較変更・取消と元要求照会を永続化します。</summary>
public sealed class UpdateScheduleStore(IDurableRepository repository, string owner, IUpdateScheduleLauncher launcher)
{
    private string Scope => "peer:" + owner + ":update-schedules";
    private string Operations => "peer:" + owner + ":update-schedule-operations";

    /// <summary>一つの予約を保存版と一緒に取得します。</summary>
    /// <returns>存在する予約かnullです。</returns>
    public async Task<UpdateScheduleView?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        return View(entry);
    }

    /// <summary>予約結果を最新の予定日時順で取得します。</summary>
    /// <returns>現在の予約スナップショットです。</returns>
    // 抑制理由: 一回の比較に使う確定スナップショットとして全項目を取得します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW160", Justification = "比較更新と期限判定の固定一覧であり、遅延列挙ではありません。")]
    public async Task<IReadOnlyList<UpdateScheduleView>> ListAsync(CancellationToken cancellationToken = default)
        => (await repository.ListAsync(Scope, cancellationToken).ConfigureAwait(false)).Select(View).OfType<UpdateScheduleView>().OrderBy(view => view.Schedule.Plan.StartAt).ToArray();

    /// <summary>ペイロード・対象実体を確認して、未開始予約だけを変更します。</summary>
    /// <returns>予約変更自身の確認結果です。</returns>
    public Task<UpdateScheduleResult> SaveAsync(UpdateScheduleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.OperationId, request, async token =>
        {
            UpdateSchedulePlan plan = request.Plan;
            if (plan?.Update is null || plan.Update.OperationId == Guid.Empty || plan.MaximumDelaySeconds is < 0 or > 604800 || plan.StartAt == default)
            {
                return new(EOutcome.NotExecuted, null, "schedule.invalidPlan");
            }
            if (DateTimeOffset.MaxValue.AddSeconds(-plan.MaximumDelaySeconds) < plan.StartAt)
            {
                return new(EOutcome.NotExecuted, null, "schedule.invalidPlan");
            }
            UpdateScheduleView? current = await ReadAsync(plan.Update.OperationId, token).ConfigureAwait(false);
            if (!string.Equals(current?.Version, request.ExpectedVersion, StringComparison.Ordinal) || current is { Schedule.State: not EUpdateScheduleState.Waiting })
            {
                return new(EOutcome.NotExecuted, current, "schedule.changedOrAlreadyStarted");
            }
            if (current is null && 128 <= (await ListAsync(token).ConfigureAwait(false)).Count(item => item.Schedule.State is EUpdateScheduleState.Waiting or EUpdateScheduleState.Starting or EUpdateScheduleState.Running or EUpdateScheduleState.Unconfirmed))
            {
                return new(EOutcome.NotExecuted, null, "schedule.pendingLimit");
            }
            IReadOnlyDictionary<string, Guid> instances;
            try
            {
                instances = await launcher.ValidateAsync(plan.Update, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                return new(EOutcome.NotExecuted, current, "schedule.validationFailed");
            }
            ScheduledUpdate scheduled = new(owner, ContractJson.Copy(plan), new Dictionary<string, Guid>(instances, StringComparer.Ordinal), EUpdateScheduleState.Waiting, EOutcome.NotExecuted);
            return await WriteAsync(request.ExpectedVersion, scheduled, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>日時到来時の開始意図と同じ保存版を競合させます。</summary>
    /// <returns>取消済みか、既に開始したため取消しなかった結果です。</returns>
    public Task<UpdateScheduleResult> CancelAsync(UpdateScheduleCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(request.OperationId, request, async token =>
        {
            UpdateScheduleView? current = await ReadAsync(request.UpdateOperationId, token).ConfigureAwait(false);
            if (current is null || !string.Equals(request.ExpectedVersion, current.Version, StringComparison.Ordinal) || current.Schedule.State != EUpdateScheduleState.Waiting)
            {
                return new(EOutcome.NotExecuted, current, "schedule.changedOrAlreadyStarted");
            }
            return await WriteAsync(current.Version, current.Schedule with { State = EUpdateScheduleState.Cancelled, Outcome = EOutcome.NotExecuted, Reason = "schedule.cancelledBeforeStart" }, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>応答喪失時にも同じ予約変更IDを照会します。</summary>
    /// <returns>未受理ならnull、開始済み結果未保存ならUnknownです。</returns>
    public async Task<UpdateScheduleResult?> OperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Operations, operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        UpdateScheduleOperation? operation = entry?.Value?.Json.Deserialize<UpdateScheduleOperation>(ContractJson.Options);
        return operation is null ? null
                                 : operation.Result ?? new(EOutcome.Unknown, null, "schedule.changeUnconfirmed");
    }

    /// <summary>開始・終了も同じ比較保存を使います。</summary>
    /// <returns>この呼出しが保存を確定したかです。</returns>
    internal async Task<UpdateScheduleResult> WriteAsync(string? expectedVersion, ScheduledUpdate schedule, CancellationToken cancellationToken = default)
    {
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(Scope, schedule.Plan.Update.OperationId.ToString("N"), expectedVersion, ContractJson.ToValue(schedule))], cancellationToken).ConfigureAwait(false);
        return new(receipt.Outcome, View(receipt.Entries.FirstOrDefault(entry => string.Equals(entry.Scope, Scope, StringComparison.Ordinal))), receipt.Reason);
    }

    /// <summary>操作所有権を一度だけ取得し、途中からの自動再実行を防ぎます。</summary>
    /// <returns>元結果または今回の変更結果です。</returns>
    private async Task<UpdateScheduleResult> ChangeAsync<T>(Guid id, T request, Func<CancellationToken, Task<UpdateScheduleResult>> change, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return new(EOutcome.NotExecuted, null, "schedule.operationIdRequired");
        }
        string key = id.ToString("N");
        string fingerprint = ContractJson.Hash(request);
        try
        {
            DurableEntry? old = await repository.ReadAsync(Operations, key, cancellationToken).ConfigureAwait(false);
            UpdateScheduleOperation? known = old?.Value?.Json.Deserialize<UpdateScheduleOperation>(ContractJson.Options);
            if (known is not null)
            {
                if (!string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new(EOutcome.NotExecuted, null, "schedule.operationMismatch");
                }
                return known.Result ?? new(EOutcome.Unknown, null, "schedule.changeUnconfirmed");
            }
            DurableReceipt begin = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(Operations, key, null, ContractJson.ToValue(new UpdateScheduleOperation(fingerprint)))], cancellationToken).ConfigureAwait(false);
            if (begin.Outcome != EOutcome.Succeeded)
            {
                return new(EOutcome.Unknown, null, "schedule.changeAlreadyReserved");
            }
            UpdateScheduleResult result = await change(cancellationToken).ConfigureAwait(false);
            DurableReceipt completed = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(Operations, key, begin.Entries.Single().Version, ContractJson.ToValue(new UpdateScheduleOperation(fingerprint, result)))], cancellationToken).ConfigureAwait(false);
            if (completed.Outcome != EOutcome.Succeeded)
            {
                return new(EOutcome.Unknown, result.View, "schedule.changeResultUnconfirmed");
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or JsonException or UnauthorizedAccessException or System.Data.Common.DbException or OperationCanceledException)
        {
            return new(EOutcome.Unknown, null, "schedule.storageUnconfirmed");
        }
    }

    /// <summary>削除履歴を生きた予約と取り違えません。</summary>
    /// <returns>保存版を保持した予約です。</returns>
    private static UpdateScheduleView? View(DurableEntry? entry)
    {
        ScheduledUpdate? schedule = entry?.Value?.Json.Deserialize<ScheduledUpdate>(ContractJson.Options);
        return schedule is null ? null
                                : new(entry!.Version, schedule);
    }
}
