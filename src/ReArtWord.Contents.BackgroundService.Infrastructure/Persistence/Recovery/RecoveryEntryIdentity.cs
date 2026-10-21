namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>現在値から分離できる保存版・取得履歴の不変な識別です。</summary>
/// <param name="Scope">共有される履歴の用途です。</param>
/// <param name="Key">用途内の履歴キーです。</param>
/// <param name="Version">固定した元の版です。</param>
/// <param name="Causality">元本文と同じ版の因果情報です。本文と一緒に背景確定します。</param>
public sealed record RecoveryEntryIdentity(string Scope, string Key, string Version, Causality.RepositoryCausalEntry? Causality = null);
