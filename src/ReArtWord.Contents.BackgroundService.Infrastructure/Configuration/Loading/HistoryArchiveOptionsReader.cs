using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using Tomlyn.Model;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>用途割当の保存指紋とは分離して、履歴の保持・一括処理条件を読みます。</summary>
internal static class HistoryArchiveOptionsReader
{
    /// <summary>省略時にも有限の件数と日数を使用します。</summary>
    /// <returns>起動前に検証した保守方針です。</returns>
    internal static HistoryArchiveOptions Read(TomlTable table)
    {
        CommonConfigurationReader.Known(table, "enabled", "keepDays", "maxRecords", "batchRecords");
        bool enabled = true;
        string enabledKey = "enabled";
        if (table.TryGetValue(enabledKey, out object? value))
        {
            enabled = value as bool? ?? throw new FormatException("storage.history.enabledは真偽値で指定してください。");
        }
        HistoryArchiveOptions options = new(enabled, CommonConfigurationReader.Integer(table, "keepDays", 7),
            CommonConfigurationReader.Integer(table, "maxRecords", 100000), CommonConfigurationReader.Integer(table, "batchRecords", 1000));
        options.Validate();
        return options;
    }
}
