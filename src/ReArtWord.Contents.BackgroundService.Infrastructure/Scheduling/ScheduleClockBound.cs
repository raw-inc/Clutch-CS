using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>同じ予定回の許容期限を、引継ぎ先となるCoreの起動実体と経過時間で保存します。</summary>
internal sealed record ScheduleClockBound(Guid Instance, long Earliest, long Latest)
{
    /// <summary>旧形式の入力と区別して、期限の根拠を入力と同じ取引で保存する領域です。</summary>
    internal static string Scope => "execution:scheduled-clocks";

    /// <summary>実体が同じ間だけ、元の許容時間の残りを復元します。</summary>
    /// <returns>この実体の時計で表した許容期限の範囲です。</returns>
    internal ScheduleAllowance Restore(RequestClockSnapshot current, TimeProvider clock)
    {
        if (current.InstanceId != Instance)
        {
            return default;
        }
        return new(clock, TimeSpan.FromTicks(Earliest - current.ElapsedTicks), TimeSpan.FromTicks(Latest - current.ElapsedTicks));
    }

    /// <summary>時計照会の往復の間に元の期限は進みます。両端を保存し、期限内とも期限切れとも断定できない時間幅を区別します。</summary>
    /// <returns>期限を確認できた起動実体だけの取得結果です。</returns>
    internal static async Task<ScheduleClockBound[]> CaptureAsync(PeerCluster cluster, bool local, IReadOnlyList<PeerInfo> members, ScheduledOccurrence occurrence,
        CancellationToken cancellationToken = default)
    {
        if (occurrence.Allowance is not { } allowance)
        {
            return [];
        }
        ConcurrentBag<ScheduleClockBound> bounds = [];
        await Parallel.ForEachAsync(members.Where(member => !local || member.InstanceId == cluster.LocalInstanceId),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (member, token) =>
            {
                TimeSpan? before = allowance.Latest;
                RequestClockSnapshot? probe;
                if (member.InstanceId == cluster.LocalInstanceId)
                {
                    probe = cluster.AdmissionClock.Capture();
                }
                else
                {
                    ClusterRequest request = ClusterRequest.Start(RequestClockSnapshot.Capability, member.PeerId, member.InstanceId, TimeSpan.FromSeconds(15), NodeValue.Null);
                    ClusterResponse response;
                    try
                    {
                        response = await cluster.InspectAsync(request, token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException || error is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        return;
                    }
                    if (response.Outcome != EOutcome.Succeeded)
                    {
                        return;
                    }
                    probe = response.Value?.Json.Deserialize<RequestClockSnapshot>(ContractJson.Options);
                }
                if (probe is not null && probe.InstanceId == member.InstanceId && 0 <= probe.ElapsedTicks && allowance.Earliest is { } after && before is { } upper)
                {
                    bounds.Add(new(member.InstanceId, checked(probe.ElapsedTicks + after.Ticks), checked(probe.ElapsedTicks + upper.Ticks)));
                }
            }).ConfigureAwait(false);
        return bounds.OrderBy(bound => bound.Instance).ToArray();
    }
}
