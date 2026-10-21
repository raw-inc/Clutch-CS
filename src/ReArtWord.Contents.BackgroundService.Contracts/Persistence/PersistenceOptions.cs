namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence;

/// <summary>用途ごとの保存方式を選び、同じ媒体は同じ保存実体を共有します。</summary>
/// <param name="DefaultBackend">未指定用途と既存保存先に使用する媒体です。</param>
/// <param name="Purposes">用途ごとの明示上書きです。稼働中の無言移行は行いません。</param>
public sealed record PersistenceOptions(EStorageBackend DefaultBackend = EStorageBackend.Sqlite, IReadOnlyDictionary<EStoragePurpose, EStorageBackend>? Purposes = null);
