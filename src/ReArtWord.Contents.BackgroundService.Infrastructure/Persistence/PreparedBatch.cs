namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>確定前に永続予約した変更内容です。</summary>
/// <param name="Fingerprint">内容の検査値。</param>
/// <param name="Mutations">予約変更。</param>
/// <param name="OperationId">予約操作。</param>
internal sealed record PreparedBatch(string Fingerprint, IReadOnlyList<DurableMutation> Mutations, Guid OperationId);
