namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>画面で確認した全対象とリリースを固定する一回の更新です。</summary>
/// <param name="OperationId">再照会に使う元操作IDです。</param>
/// <param name="Release">固定した配布元と方針です。</param>
/// <param name="Targets">host順の優先順位を持つ固定ピア集合です。</param>
public sealed record FleetUpdateRequest(Guid OperationId, FleetReleaseSelection Release, IReadOnlyList<FleetUpdateTarget> Targets);

