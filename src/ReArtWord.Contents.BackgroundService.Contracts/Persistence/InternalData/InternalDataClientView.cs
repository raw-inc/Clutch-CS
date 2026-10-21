using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>接続先実体・最終確認時刻と、未共有の端末値を明示します。</summary>
public sealed record InternalDataClientView(string ClusterId, string PeerId, Guid InstanceId, InternalDataTarget Target,
    DateTimeOffset? ConfirmedAt, bool Online, AvailabilityValue Current, AvailabilityPolicy Policy,
    AvailabilityValue? Remote = null, Guid? PendingCommandId = null, AvailabilityValue? Confirmed = null);
