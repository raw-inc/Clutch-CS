namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>同一機器の累積転送バイト数。NICではReadが受信、Writeが送信です。</summary>
internal readonly record struct TransferCounter(string Identity, ulong Read, ulong Write);
