using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>通信ノードから安全な生内容記録へ接続する境界です。</summary>
public interface INodeTrace
{
    /// <summary>一件に固定した方針と秘密保護を適用して本文だけを記録します。</summary>
    /// <returns>記録受付です。記録不能は本来通信を失敗させません。</returns>
    public ValueTask WriteRawAsync(NodeExecutionContext context, string protocol, bool outgoing, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
