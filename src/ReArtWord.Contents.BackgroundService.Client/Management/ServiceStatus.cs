using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>管理Peerの状態と実行中の対象です。</summary>
internal sealed record ServiceStatus(string PeerId, Guid InstanceId, string? Coordinator, EPeerStatus Status,
    IReadOnlyList<PeerInfo> Peers, IReadOnlyList<SourceView> Sources, IReadOnlyList<Guid> RunningNodes,
    string? StartupProblem, JsonElement Maintenance, string? SynchronizationProblem = null,
    Contracts.Observability.WorkflowEventStatistics? Events = null, ScheduleRetentionStatus? Schedules = null, string? ScheduleProblem = null,
    IReadOnlyList<Contracts.Management.Preview.NodeValueObservation>? NodeValues = null, bool LiveWorkflowUpdates = false,
    HistorySynchronizationStatus? HistorySynchronization = null);
