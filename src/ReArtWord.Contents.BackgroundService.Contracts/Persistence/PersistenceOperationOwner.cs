namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence;

/// <summary>保存操作の論理範囲です。他Peerへ物理保存先を強制しません。</summary>
/// <param name="OperationId">元の保存操作IDです。</param>
/// <param name="Fingerprint">元の要求指紋です。</param>
/// <param name="Scopes">操作が扱う保存スコープ集合。旧空操作記録では空集合です。</param>
public sealed record PersistenceOperationOwner(Guid OperationId, string Fingerprint, IReadOnlyList<string> Scopes);
