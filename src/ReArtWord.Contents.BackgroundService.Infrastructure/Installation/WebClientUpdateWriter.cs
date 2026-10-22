using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>更新資材の回収前に、元の入力とhelperの実終了を照合する記録です。</summary>
internal sealed record WebClientUpdateWriter(Guid OperationId, string JobHash, ProcessIdentity Process);
