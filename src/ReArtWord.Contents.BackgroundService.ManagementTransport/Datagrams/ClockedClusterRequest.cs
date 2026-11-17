namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>内容照合に使う元本文と、対象実体の時計で表した開始期限を分離します。</summary>
public sealed record ClockedClusterRequest(ClusterRequest Request, Guid ClockInstanceId, long StartByTicks, DateTimeOffset SourceNow)
{
    /// <summary>対応確認済みの相手へだけ送る要求形式です。</summary>
    public static string Kind => "request.clocked";
}
