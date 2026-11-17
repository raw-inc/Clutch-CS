namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>元の要求に、送信先Coreの時計で表した期限を添えます。元の本文を変更しないため、再送時の内容照合にも同じハッシュを使えます。</summary>
public sealed record ClockedClusterRequest(ClusterRequest Request, Guid ClockInstanceId, long StartByTicks, DateTimeOffset SourceNow)
{
    /// <summary>時計照会への対応を確認できたCoreにだけ送るメッセージ種別です。</summary>
    public static string Kind => "request.clocked";
}
