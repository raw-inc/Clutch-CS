using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;

// LEGACY-FORMAT: manual-safety-v1 — 旧操作IDの補完は意味を保存して読取り、新旧の停止操作が保持対象外となった後に撤去します。
/// <summary>実際に観測した旧操作だけを置換し、分断側の未観測停止を失いません。</summary>
internal static class ManualSafetyState
{
    /// <summary>旧形式も書き直さず一意な元操作として読みます。</summary>
    /// <returns>保存された操作一覧です。</returns>
    public static ManualSafetyEntry[] Read(IReadOnlyList<DurableEntry> entries, bool detached)
    {
        List<ManualSafetyEntry> result = [];
        foreach (DurableEntry entry in entries)
        {
            ManualSafetySetting? setting = entry.Value?.Json.Deserialize<ManualSafetySetting>();
            if (setting is null)
            {
                continue;
            }
            if (setting.OperationId == Guid.Empty)
            {
                Guid id = new(Convert.FromHexString(ContractJson.Hash(new { entry.Scope, entry.Key, entry.Version })).AsSpan(0, 16));
                setting = setting with { OperationId = id };
            }
            result.Add(new(setting, entry.Version, detached));
        }
        return result.ToArray();
    }

    /// <summary>置換済み操作と、同じ受付実体内の先行操作を除きます。</summary>
    /// <returns>現在判断する操作だけです。</returns>
    public static ManualSafetyEntry[] Frontier(IEnumerable<ManualSafetyEntry> values)
    {
        ManualSafetyEntry[] entries = values.ToArray();
        HashSet<Guid> superseded = entries.SelectMany(entry => entry.Setting.Supersedes ?? []).ToHashSet();
        Dictionary<(string WorkflowId, string Scope, Guid InstanceId), long> latest = entries.Where(entry => entry.Setting.Acceptance is not null)
            .GroupBy(entry => (entry.Setting.WorkflowId, entry.Setting.Scope, entry.Setting.Acceptance!.InstanceId))
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.Setting.Acceptance!.Sequence));
        return entries.Where(entry => !superseded.Contains(entry.Setting.OperationId)
            && (entry.Setting.Acceptance is null || entry.Setting.Acceptance.Sequence == latest[(entry.Setting.WorkflowId, entry.Setting.Scope, entry.Setting.Acceptance.InstanceId)])).ToArray();
    }
}
