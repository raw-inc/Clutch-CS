namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;

/// <summary>旧版の管理契約を変更せず照会する更新データ転送能力です。</summary>
/// <param name="Version">0は利用不能、1はマニフェスト順のTCP連続転送です。</param>
/// <param name="Port">選択NICのTCP待受ポートです。</param>
/// <param name="BufferBytes">一回の転送で再利用する作業領域の上限です。</param>
public sealed record UpdateStreamCapabilities(int Version, int Port, int BufferBytes);
