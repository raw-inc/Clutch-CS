namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>サイズ検査可能なUDP分割単位です。</summary>
/// <param name="Id">配送ID。</param>
/// <param name="Index">位置。</param>
/// <param name="Count">総数。</param>
/// <param name="Length">全体長。</param>
/// <param name="Hash">全体SHA256。</param>
/// <param name="Payload">部分本文。</param>
internal sealed record DatagramFragment(Guid Id, int Index, int Count, int Length, string Hash, byte[] Payload);
