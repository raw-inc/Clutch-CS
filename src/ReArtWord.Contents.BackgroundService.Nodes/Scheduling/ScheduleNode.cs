using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scheduling;

/// <summary>日時到来を後続へ渡し、起動処理を内包しない時間指定ノードです。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class ScheduleNode : ISourceNode
{
    private const string SettingMissed = "missed";
    private const string SettingToleranceMilliseconds = "toleranceMilliseconds";
    private const string SettingCount = "count";

    private const string SkipPolicy = "skip";
    private const string WithinPolicy = "within";

    /// <summary>日時・期間・例外日と未実行方針を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("scheduling.timer", "Schedule / interval timer", "Scheduling",
        """{"type":"object","required":["mode"],"properties":{"timeZoneId":{"type":"string","default":"UTC","description":"Time zone for this schedule, for example UTC or Asia/Tokyo. Blank or omitted keeps the shared time zone; new nodes default to UTC."},"mode":{"type":"string","enum":["interval","once","daily","weekdays"]},"intervalMilliseconds":{"type":"number","minimum":1,"maximum":2147483646,"x-visibleWhen":{"property":"mode","values":["interval"]},"default":1000},"at":{"type":"string","x-visibleWhen":{"property":"mode","values":["once"]},"x-requiredWhen":{"property":"mode","values":["once"]}},"time":{"type":"string","x-visibleWhen":{"property":"mode","values":["daily","weekdays"]},"x-requiredWhen":{"property":"mode","values":["daily","weekdays"]}},"start":{"type":"string"},"end":{"type":"string"},"weekdays":{"type":"array","items":{"type":"integer","minimum":0,"maximum":6},"x-visibleWhen":{"property":"mode","values":["weekdays"]},"x-requiredWhen":{"property":"mode","values":["weekdays"]}},"includeDates":{"type":"array","items":{"type":"string"}},"excludeDates":{"type":"array","items":{"type":"string"}},"missed":{"type":"string","enum":["delay","skip","within"]},"toleranceMilliseconds":{"type":"number","minimum":0,"maximum":2147483646,"x-visibleWhen":{"property":"missed","values":["within"],"default":"delay"},"x-requiredWhen":{"property":"missed","values":["within"],"default":"delay"}},"count":{"type":"integer","minimum":1},"timeQualityPolicy":{"type":"string","enum":["warn","stop"]}}}""",
        [new PortDescriptor("value"), new PortDescriptor("clockValid")], [new PortDescriptor("value"), new PortDescriptor("scheduledAt"), new PortDescriptor("observedAt")], routes: ["success", "alreadyCompleted"], kind: ENodeKind.Source) with
        {
            Description = "Emit once at a date and time, daily or on weekdays; or repeat at a fixed interval within an optional start/end period. Uses the selected time zone; blank keeps the shared time zone.",
        };

    /// <summary>時刻指定の構文と必要設定を検証します。</summary>
    /// <returns>意味を解釈できない設定です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (issues.Count != 0)
        {
            return issues;
        }
        try
        {
            _ = new SchedulePlan(definition.Settings, Zone(definition.Settings), DateTimeOffset.UnixEpoch);
            NodeSettings settings = new(definition.Settings);
            if (string.Equals(settings.ReadText(SettingMissed), WithinPolicy, StringComparison.Ordinal) && settings.Get(SettingToleranceMilliseconds).ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                issues.Add(new ValidationIssue("schedule.tolerance.required", "許容遅延時間が必要です。", definition.Id));
            }
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            issues.Add(new("time.zone.invalid", "実行PCで利用できるタイムゾーン識別子を指定してください。例: UTC、Asia/Tokyo。", definition.Id, "/timeZoneId"));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException or InvalidOperationException)
        {
            issues.Add(new ValidationIssue("schedule.invalid", "日時・曜日・有効期間の設定が不正です。", definition.Id));
        }
        return issues;
    }

    /// <summary>新しい時間契機ごとに現在の世代を固定します。</summary>
    /// <returns>予定終了、取消または設定不能の結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        try
        {
            DateTimeOffset activatedAt = context.Initial.TimeProvider.GetUtcNow();
            NodeExecutionContext snapshot = context.Initial;
            ScheduleCursor cursor = await ReadCursorAsync(context, snapshot, activatedAt, lifetime.Token).ConfigureAwait(false);
            if (cursor.Outcome != EOutcome.Succeeded)
            {
                return CursorFailure(cursor);
            }
            TimeZoneInfo zone = Zone(snapshot.Settings, snapshot.TimeZone);
            SchedulePlan plan = new(snapshot.Settings, zone, cursor.Anchor);
            DateTimeOffset? next = plan.Next(cursor.LastScheduledAt ?? DateTimeOffset.MinValue);
            if (cursor.Exhausted || 0 < cursor.Count && (next is null || new NodeSettings(snapshot.Settings).ReadInt32(SettingCount, int.MaxValue) <= cursor.Count))
            {
                return NodeResult.Success(route: "alreadyCompleted");
            }
            string? previousQuality = null;
            bool previousQualityStop = false;
            bool refreshCursor = false;
            await context.ReportStatusAsync(new NodeSourceStatus(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            while (next is not null && !context.IsStopRequested)
            {
                NodeExecutionContext current = context.BeginItem();
                DateTimeOffset now = current.TimeProvider.GetUtcNow();
                TimeZoneInfo currentZone = Zone(current.Settings, current.TimeZone);
                if (refreshCursor || !string.Equals(currentZone.Id, zone.Id, StringComparison.Ordinal) || !System.Text.Json.JsonElement.DeepEquals(current.Settings, snapshot.Settings))
                {
                    snapshot = current;
                    cursor = await ReadCursorAsync(context, snapshot, now, lifetime.Token).ConfigureAwait(false);
                    if (cursor.Outcome != EOutcome.Succeeded)
                    {
                        return CursorFailure(cursor);
                    }
                    zone = currentZone;
                    plan = new SchedulePlan(snapshot.Settings, zone, cursor.Anchor);
                    next = plan.Next(cursor.LastScheduledAt ?? DateTimeOffset.MinValue);
                    if (cursor.Exhausted || 0 < cursor.Count && (next is null || new NodeSettings(snapshot.Settings).ReadInt32(SettingCount, int.MaxValue) <= cursor.Count))
                    {
                        return NodeResult.Success(route: "alreadyCompleted");
                    }
                    refreshCursor = false;
                    previousQuality = null;
                    continue;
                }
                NodeValue? clockValid = NodeSupport.Input(current, "clockValid");
                bool validClock = clockValid?.Kind == EValueKind.Json && clockValid.Json.ValueKind == System.Text.Json.JsonValueKind.True;
                string quality = "schedule.clock.unknown";
                if (validClock)
                {
                    quality = "schedule.clock.valid";
                }
                else
                {
                    if (clockValid?.Kind == EValueKind.Json && clockValid.Json.ValueKind == System.Text.Json.JsonValueKind.False)
                    {
                        quality = "schedule.clock.invalid";
                    }
                }
                string stopPolicy = "stop";
                bool stopForQuality = !validClock && string.Equals(new NodeSettings(current.Settings).ReadText("timeQualityPolicy"), stopPolicy, StringComparison.Ordinal);
                if (!string.Equals(previousQuality, quality, StringComparison.Ordinal) || previousQualityStop != stopForQuality)
                {
                    ESourceState state = ESourceState.Listening;
                    if (stopForQuality)
                    {
                        state = ESourceState.Unavailable;
                    }
                    await context.ReportStatusAsync(new NodeSourceStatus(state, quality, "時刻品質の入力状態に応じて時間指定を判断します。"), lifetime.Token).ConfigureAwait(false);
                    previousQuality = quality;
                    previousQualityStop = stopForQuality;
                }
                if (stopForQuality)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), current.TimeProvider, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (now < next)
                {
                    TimeSpan wait = next.Value - now;
                    if (TimeSpan.FromMilliseconds(250) < wait)
                    {
                        wait = TimeSpan.FromMilliseconds(250);
                    }
                    await Task.Delay(wait, current.TimeProvider, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                DateTimeOffset due = plan.Latest(next.Value, now);
                NodeSettings settings = new(current.Settings);
                double tolerance = settings.ReadNumber("toleranceMilliseconds", 1000);
                bool missed = TimeSpan.FromMilliseconds(tolerance) < now - due;
                string? policy = settings.ReadText("missed");
                current = current with
                {
                    Schedule = new ScheduledOccurrence(cursor.Key, current.NodeId, context.Initial.ExecutionId, due,
                        Enum.Parse<EScheduleMissedPolicy>(policy ?? "delay", true), TimeSpan.FromMilliseconds(tolerance)),
                };
                NodeResult result;
                if (missed && (string.Equals(policy, SkipPolicy, StringComparison.Ordinal) || string.Equals(policy, WithinPolicy, StringComparison.Ordinal)))
                {
                    result = NodeResult.Failure("schedule.missed", "時間指定の許容遅延を超えた回を見送りました。", EOutcome.NotExecuted);
                }
                else
                {
                    result = new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                    {
                        ["value"] = NodeSupport.Input(current) ?? NodeValue.FromObject(due),
                        ["scheduledAt"] = NodeValue.FromObject(due),
                        ["observedAt"] = NodeValue.FromObject(now),
                    });
                }
                cursor = await AdvanceCursorAsync(context, cursor, new NodeEmission(current, result), lifetime.Token).ConfigureAwait(false);
                if (IsRejectedConflict(cursor))
                {
                    await WaitForCursorAsync(context, current, lifetime.Token).ConfigureAwait(false);
                    refreshCursor = true;
                    continue;
                }
                if (cursor.Outcome != EOutcome.Succeeded)
                {
                    return CursorFailure(cursor);
                }
                await context.EmitAsync(current, result, lifetime.Token).ConfigureAwait(false);
                if (settings.ReadInt32(SettingCount, int.MaxValue) <= cursor.Count)
                {
                    break;
                }
                next = plan.Next(due);
            }
            if (next is null && cursor.Count == 0 && context.Schedule is not null)
            {
                cursor = await context.Schedule.FinishAsync(cursor, lifetime.Token).ConfigureAwait(false);
                if (cursor.Outcome != EOutcome.Succeeded)
                {
                    return CursorFailure(cursor);
                }
            }
        }
        catch (OperationCanceledException) when (context.IsStopRequested && !cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("schedule.cancelled", "時間指定を取り消しました。", EOutcome.Cancelled);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return NodeResult.Failure("schedule.unavailable", "時間指定を継続できません。設定とタイムゾーンを確認してください。");
        }
        return NodeResult.Success();
    }

    /// <summary>旧設定の共通ゾーンを維持し、明示したノードのゾーンを優先します。</summary>
    /// <returns>予定時刻と受付位置の両方へ適用するゾーンです。</returns>
    private static TimeZoneInfo Zone(System.Text.Json.JsonElement settings, TimeZoneInfo? shared = null)
    {
        string? id = new NodeSettings(settings).ReadText("timeZoneId");
        return string.IsNullOrWhiteSpace(id) ? shared ?? TimeZoneInfo.Utc
                                             : TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    /// <summary>共有記録を読み、単独のノード実行ではその実行内の基準を使用します。</summary>
    /// <returns>基準時刻と受付済み回。</returns>
    private static async ValueTask<ScheduleCursor> ReadCursorAsync(NodeSourceContext context, NodeExecutionContext snapshot, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        string key = ScheduleTimeline.Key(snapshot.Settings, Zone(snapshot.Settings, snapshot.TimeZone));
        if (context.Schedule is not null)
        {
            while (!context.IsStopRequested && !cancellationToken.IsCancellationRequested)
            {
                ScheduleCursor cursor = await context.Schedule.ReadAsync(key, now, cancellationToken).ConfigureAwait(false);
                if (!IsRejectedConflict(cursor))
                {
                    return cursor;
                }
                await WaitForCursorAsync(context, snapshot, cancellationToken).ConfigureAwait(false);
                _ = context.BeginItem();
            }
            throw new OperationCanceledException(cancellationToken);
        }
        return new ScheduleCursor(key, null, now);
    }

    /// <summary>全参加者で受付中止を確認した版競合だけを読み直しの対象にします。</summary>
    /// <returns>結果不明の回を再発生させず、進行位置を再照合できるかです。</returns>
    private static bool IsRejectedConflict(ScheduleCursor cursor)
        => cursor.Outcome == EOutcome.Failed && string.Equals(cursor.Reason, "Persistence.VersionConflict", StringComparison.Ordinal);

    /// <summary>競合中は発火を保留し、停止要求に応答しながら再読取りを間引きます。</summary>
    /// <returns>有限の待機と未受付状態の通知です。</returns>
    private static async ValueTask WaitForCursorAsync(NodeSourceContext context, NodeExecutionContext snapshot, CancellationToken cancellationToken = default)
    {
        await context.ReportStatusAsync(new(ESourceState.Unavailable, "schedule.cursorRetry", "時間指定の受付が競合したため、確定済みの進行位置を再確認します。"), cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(250), snapshot.TimeProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通知より先に予定位置を進め、確認できない回を暗黙に再発生させません。</summary>
    /// <returns>更新後の位置と確認結果。</returns>
    private static ValueTask<ScheduleCursor> AdvanceCursorAsync(NodeSourceContext context, ScheduleCursor cursor, NodeEmission emission, CancellationToken cancellationToken = default)
    {
        if (context.Schedule is not null)
        {
            return context.Schedule.AdvanceAsync(cursor, emission, cancellationToken);
        }
        return ValueTask.FromResult(cursor with { LastScheduledAt = emission.Context.Schedule!.ScheduledAt, Count = cursor.Count + 1 });
    }

    /// <summary>受付済みか確認できない回を、未受付として扱わないための結果です。</summary>
    /// <returns>記録の確認不能を示す結果。</returns>
    private static NodeResult CursorFailure(ScheduleCursor cursor)
        => NodeResult.Failure(cursor.Reason ?? "schedule.cursorUnconfirmed", "時間指定の受付記録を確認できないため、新しい回を発生させません。", cursor.Outcome);
}
