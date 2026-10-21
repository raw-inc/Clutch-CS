using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Persistence;

/// <summary>Hostを起動せず、明示した設定のSQLiteへ停止中専用の保守を実施します。</summary>
internal static class HistoryMaintenanceCommand
{
    /// <summary>全展開は旧形式への復帰、compactは圧縮状態のまま物理容量を縮める操作です。</summary>
    /// <returns>確認した保守結果を出力した正常終了コードです。</returns>
    internal static async Task<int> RunAsync(string configurationPath, string operation, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(configurationPath);
        CommonConfiguration configuration = CommonConfigurationReader.Read(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Path.GetDirectoryName(path)!);
        bool expand = operation switch
        {
            "expand" => true,
            "compact" => false,
            _ => throw new ArgumentException("履歴保守はexpandまたはcompactで指定してください。", nameof(operation)),
        };
        Contracts.Persistence.History.HistoryArchiveStatus result = await HistoryOfflineMaintenance.RunAsync(configuration.DataDirectory, configuration.Cluster.PeerId,
            configuration.Persistence, expand, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(result, ContractJson.Options)).ConfigureAwait(false);
        return 0;
    }
}
