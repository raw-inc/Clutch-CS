namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>ブラウザーへ常時送る、原本を含まない一行です。</summary>
internal sealed record SignalScopeRow(long PacketId, string SourceId, string ObservationPoint, string Time, int LinkType, int OriginalLength,
    string Source, string Destination, int? SourcePort, int? DestinationPort, string Protocol, string Transport, string Summary, string Fingerprint,
    long QueryId = 0, bool Matched = true);
