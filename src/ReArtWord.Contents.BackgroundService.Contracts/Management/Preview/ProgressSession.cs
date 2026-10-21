namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>一回のWF実行が所有する進捗です。反復の分離時だけScopeIdを指定します。</summary>
/// <param name="RunId">元のWF実行です。</param>
/// <param name="OwnerPeerId">実行全体を所有するPeerです。</param>
/// <param name="OwnerInstanceId">所有Peerの起動実体です。</param>
/// <param name="StartedAt">同時実行を見分ける開始時刻です。</param>
/// <param name="ScopeId">分離した反復要素です。空の場合はWF全体へ集約します。</param>
public sealed record ProgressSession(Guid RunId, string OwnerPeerId, Guid OwnerInstanceId, DateTimeOffset StartedAt, Guid ScopeId = default);
