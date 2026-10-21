namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>本文の整合性を確認した観測です。既知内容と同じ場合だけ本文を省略します。</summary>
/// <param name="Key">用途内の識別子です。</param>
/// <param name="Fingerprint">版だけでなく保存本文全体を識別する不透明な値です。</param>
/// <param name="Entry">変更時の本文です。nullは同じ指紋の本文を既に確認済みであることを示します。</param>
public readonly record struct DurableObservation(string Key, string Fingerprint, DurableEntry? Entry);
