namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>最後に確認した実体に紐付ける内部状態の対象です。</summary>
public sealed record InternalDataClientTarget(InternalDataTarget Target, Guid? InstanceId = null);
