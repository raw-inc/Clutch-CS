using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>保存前の構造検証結果です。</summary>
internal sealed record ValidationView(bool Valid, IReadOnlyList<ValidationIssue> Issues, string? Revision);
