namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>離脱前の共有元版と、外部へ公開していない現在値です。</summary>
internal sealed record DetachedStateEntry(DurableEntry? Base, DurableEntry Current, bool Pending = true, bool Submitted = false);
