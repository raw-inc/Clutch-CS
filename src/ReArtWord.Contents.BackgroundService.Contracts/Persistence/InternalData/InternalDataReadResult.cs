using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>未保存有効値と強い読取りで確認した保存値を区別します。</summary>
public sealed record InternalDataReadResult(AvailabilityValue Current, NodeValue? ConfirmedValue, string? ConfirmedRevision, bool ConfirmedKnown, AvailabilityPolicy Policy);
