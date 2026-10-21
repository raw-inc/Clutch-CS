namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>伏字済みバイトだけを保持するキュー項目です。</summary>
/// <param name="Payload">伏字済みJSON。空はflush境界。</param>
/// <param name="Forward">このPeerから転送するか。</param>
/// <param name="Receipt">必要時の永続受付確認。</param>
internal sealed record LogWorkItem(ReadOnlyMemory<byte> Payload, bool Forward, TaskCompletionSource<bool>? Receipt = null);
