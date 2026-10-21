using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>固定集合へ送る同一操作内容です。</summary>
/// <param name="Id">操作ID。</param>
/// <param name="Mutations">変更内容。</param>
internal sealed record ReplicationRequest(Guid Id, IReadOnlyList<DurableMutation> Mutations);
