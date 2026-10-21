using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using Tomlyn.Model;
using static ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading.CommonConfigurationReader;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>用途に適用できない保存方針を黙って受理しません。</summary>
internal static class AvailabilityOptionsReader
{
    /// <summary>内部設定と通常状態だけに候補の利用方針を設定します。</summary>
    /// <returns>有限の保持量を持つ検証済み方針です。</returns>
    public static AvailabilityOptions Read(TomlTable table)
    {
        Known(table, "maximumItems", "maximumBytes", "configuration", "runtime", "processes", "diagnostics");
        int items = Integer(table, "maximumItems", 256);
        int bytes = Integer(table, "maximumBytes", 1048576);
        if (items is < 1 or > 10000 || bytes is < 1024 or > 67108864)
        {
            throw new FormatException("保存候補の保持上限が範囲外です。");
        }
        Dictionary<EStoragePurpose, AvailabilityPolicy> policies = [];
        foreach (EStoragePurpose purpose in Enum.GetValues<EStoragePurpose>())
        {
            string name = purpose switch
            {
                EStoragePurpose.Configuration => "configuration",
                EStoragePurpose.Runtime => "runtime",
                EStoragePurpose.Processes => "processes",
                EStoragePurpose.Diagnostics => "diagnostics",
                _ => throw new FormatException("保存用途を判別できません。"),
            };
            if (!table.ContainsKey(name))
            {
                continue;
            }
            TomlTable policy = Table(table, name);
            Known(policy, "saveFailure", "offline", "fallbackBackend");
            EStorageBackend? fallback = null;
            string fallbackName = "fallbackBackend";
            if (policy.ContainsKey(fallbackName))
            {
                fallback = Choice(policy, "fallbackBackend", EStorageBackend.Sqlite);
            }
            AvailabilityPolicy selected = new(Choice(policy, "saveFailure", EAvailabilitySaveFailure.KeepConfirmed),
                Choice(policy, "offline", EAvailabilityOffline.LastConfirmedReadOnly), fallback);
            if (purpose is EStoragePurpose.Processes or EStoragePurpose.Diagnostics && selected != new AvailabilityPolicy())
            {
                throw new FormatException("processesとdiagnosticsは未保存候補の採用・切断中編集の対象外です。");
            }
            policies.Add(purpose, selected);
        }
        return new(policies, items, bytes);
    }
}
