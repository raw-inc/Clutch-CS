using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;

/// <summary>sourceが待っている応答ハンドルの寿命を所有します。</summary>
internal sealed class ReplyRequestLease : IDisposable
{
    private readonly CommunicationReplyRegistry registry;
    private readonly Guid id;
    private readonly PendingReply request;

    /// <summary>一つのハンドルをsourceへ渡します。</summary>
    internal ReplyRequestLease(CommunicationReplyRegistry registry, Guid id, PendingReply request)
    {
        this.registry = registry;
        this.id = id;
        this.request = request;
        Handle = NodeValue.FromObject(new { ownerPeer = request.OwnerPeer, id });
    }

    /// <summary>後続へ渡すopaque識別子です。</summary>
    internal NodeValue Handle
    {
        get;
    }

    /// <summary>受信済み一件の返信終了を待ちます。</summary>
    internal Task<NodeResult> Completion => request.Completion;

    /// <summary>後着の返信を拒否しハンドルを解放します。</summary>
    public void Dispose()
    {
        registry.Release(id);
    }
}
