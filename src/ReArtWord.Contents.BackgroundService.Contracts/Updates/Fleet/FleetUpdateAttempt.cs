namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>副作用の確認単位を失わない一ピアの一試行です。</summary>
/// <param name="OperationId">既存Updaterに渡す更新IDです。</param>
/// <param name="Result">Updaterで確認した進行または終端結果です。</param>
/// <param name="PreviousInstanceId">この試行を許可した実体です。復元後の再試行も区別します。</param>
/// <param name="Problem">応答が得られなかった段階と、要求内容を含まない診断コードです。</param>
public sealed record FleetUpdateAttempt(Guid OperationId, UpdatePeerResult? Result = null, Guid? PreviousInstanceId = null, string? Problem = null);
