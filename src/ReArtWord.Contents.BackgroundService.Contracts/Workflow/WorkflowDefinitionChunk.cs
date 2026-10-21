namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>検証前に定義として使わない、版固定のUTF8断片です。</summary>
/// <param name="Revision">完全定義の版。</param>
/// <param name="Offset">断片のバイト位置。</param>
/// <param name="TotalBytes">完全なUTF8長。</param>
/// <param name="Hash">完全UTF8のSHA256。</param>
/// <param name="DataBase64">UTF8断片のbase64。</param>
/// <param name="NextOffset">最後ならnull。</param>
public sealed record WorkflowDefinitionChunk(string Revision, int Offset, int TotalBytes, string Hash, string DataBase64, int? NextOffset);
