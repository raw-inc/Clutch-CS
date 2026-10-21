namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>副作用を開始しない要求操作記録の照会結果です。</summary>
public sealed record ClusterRequestStatus(bool Running, ClusterResponse? Response = null, string? Reason = null);
