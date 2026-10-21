using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>削除後も競合判定に残す保存履歴です。</summary>
/// <param name="Scope">保存用途。</param>
/// <param name="Key">識別子。</param>
/// <param name="Version">保存版。</param>
/// <param name="Value">値、または削除済みのnull。</param>
/// <param name="ParentVersion">変更前の版。</param>
/// <param name="MergedVersions">明示解消した分岐版。</param>
public sealed record DurableEntry(string Scope, string Key, string Version, NodeValue? Value, string? ParentVersion = null, IReadOnlyList<string>? MergedVersions = null);
