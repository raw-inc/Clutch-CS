namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

/// <summary>本文履歴を展開せず、現在版どうしの因果関係を確認する保存情報です。</summary>
/// <param name="Scope">対象項目の保存用途です。</param>
/// <param name="Key">対象用途内のキーです。</param>
/// <param name="Version">対応する本文の版です。</param>
/// <param name="Clock">書込み元ごとの確定位置を正規化したJSONです。</param>
public sealed record RepositoryCausalEntry(string Scope, string Key, string Version, string Clock);
