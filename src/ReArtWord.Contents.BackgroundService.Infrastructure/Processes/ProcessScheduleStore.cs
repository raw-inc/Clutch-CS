using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>登録の変更と予定の受付位置を同じ比較保存で管理します。</summary>
internal sealed class ProcessScheduleStore(IDurableRepository repository, string scope, Func<SystemBootSession?> boot)
{
    /// <summary>新規・変更・削除した予定だけを保存対象とし、論理名の編集では実行回を戻しません。</summary>
    /// <returns>登録定義と一緒に確定する変更です。</returns>
    internal async ValueTask<IReadOnlyList<DurableMutation>> RegistrationAsync(ProcessDefinition current, ProcessDefinition? previous, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        List<DurableMutation> mutations = [];
        foreach (ProcessSchedule schedule in current.Schedules ?? [])
        {
            string key = Key(current.Id, schedule.Id);
            DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            ProcessScheduleCursor? cursor = entry?.Value?.Json.Deserialize<ProcessScheduleCursor>(ContractJson.Options);
            string timeline = ProcessSchedulePlan.Create(schedule, now).Timeline;
            bool previouslyEnabled = previous?.Mode == EProcessMode.Managed && previous.Schedules?.Any(item => string.Equals(item.Id, schedule.Id, StringComparison.Ordinal) && item.Enabled) == true;
            if (cursor is null || !string.Equals(cursor.Timeline, timeline, StringComparison.Ordinal) || schedule.Enabled && !previouslyEnabled)
            {
                string? bootId = null;
                if (ProcessSchedulePlan.IsBoot(schedule))
                {
                    bootId = boot()?.Id;
                }
                mutations.Add(new(scope, key, entry?.Version, Encode(new(timeline, now, BootId: bootId))));
            }
        }
        foreach (ProcessSchedule removed in (previous?.Schedules ?? []).Where(old => current.Schedules?.Any(item => string.Equals(item.Id, old.Id, StringComparison.Ordinal)) != true))
        {
            string key = Key(current.Id, removed.Id);
            DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            if (entry?.Value is not null)
            {
                mutations.Add(new(scope, key, entry.Version, null));
            }
        }
        return mutations;
    }

    /// <summary>既知の予定を一件だけ読み、確定済みの受付位置を返します。</summary>
    /// <returns>保存済み状態と比較版です。</returns>
    internal async ValueTask<(string? Version, ProcessScheduleCursor? Cursor)> ReadAsync(string application, string schedule, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(scope, Key(application, schedule), cancellationToken).ConfigureAwait(false);
        return (entry?.Version, entry?.Value?.Json.Deserialize<ProcessScheduleCursor>(ContractJson.Options));
    }

    /// <summary>起動の前に受付位置を確定し、結果不明時には同じ時刻を再送しません。</summary>
    /// <returns>比較保存の成否です。</returns>
    internal async ValueTask<EOutcome> SaveAsync(string application, string schedule, string? version, ProcessScheduleCursor cursor, CancellationToken cancellationToken = default)
        => (await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, Key(application, schedule), version, Encode(cursor))], cancellationToken).ConfigureAwait(false)).Outcome;

    /// <summary>自由な論理名や識別子の区切り文字が衝突しないキーです。</summary>
    /// <returns>一登録・一予定に固定したキーです。</returns>
    private static string Key(string application, string schedule) => ContractJson.Hash(new { application, schedule });

    /// <summary>共通の列挙値・日時の規則で現在値を保存します。</summary>
    /// <returns>予定の現在状態です。</returns>
    private static NodeValue Encode(ProcessScheduleCursor value) => NodeValue.FromJson(JsonSerializer.SerializeToElement(value, ContractJson.Options));
}
