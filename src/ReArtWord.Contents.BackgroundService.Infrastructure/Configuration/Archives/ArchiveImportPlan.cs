using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>復元内容、復元前の設定、参加ピアを記録した復元計画。</summary>
internal sealed record ArchiveImportPlan(ArchiveImportPreview Preview, ArchiveTransfer File, ClusterArchiveConfiguration Before,
    ClusterArchiveConfiguration Incoming, IReadOnlyList<PeerInfo> Members)
{
    /// <summary>中断後の確認でも、計画作成時の操作IDを再計算します。旧計画は旧方式のままです。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
