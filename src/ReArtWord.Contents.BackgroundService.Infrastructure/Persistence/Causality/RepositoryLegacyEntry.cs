namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

// LEGACY-FORMAT: repository-ancestry-v1 — 旧祖先の同期契約は全Peerの移行と保持期間経過後に撤去します。
/// <summary>移行前の祖先を照合する、本文を持たない有限の索引です。</summary>
/// <param name="Scope">共有用途です。</param>
/// <param name="Key">共有項目です。</param>
/// <param name="Version">旧版です。</param>
/// <param name="ValueHash">同じ版の異なる本文を区別する検査値です。</param>
/// <param name="Parents">旧版が包含した親です。</param>
public sealed record RepositoryLegacyEntry(string Scope, string Key, string Version, string ValueHash, IReadOnlyList<string> Parents);
