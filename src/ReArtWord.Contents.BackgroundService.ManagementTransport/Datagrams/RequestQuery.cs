namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>要求本体を再配送せず内容同一性を照会します。</summary>
public sealed record RequestQuery(Guid Id, string Fingerprint);
