using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

namespace ReArtWord.Contents.BackgroundService.Contracts.State;

/// <summary>表示カードと観測対象を結び付けます。</summary>
public sealed record StateStreamTarget(string Id, InternalDataTarget Target);
