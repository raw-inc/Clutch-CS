using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;

/// <summary>診断の記録不能を通信の再試行や失敗へ変換しません。</summary>
internal static class CommunicationTrace
{
    /// <summary>一件の通信内容だけを既存の安全なログ境界へ渡します。</summary>
    /// <returns>記録受付または省略の完了です。</returns>
    public static async ValueTask WriteAsync(NodeExecutionContext context, string protocol, bool outgoing, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (context.Trace is null)
        {
            return;
        }
        try
        {
            await context.Trace.WriteRawAsync(context, protocol, outgoing, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 診断実装の失敗を副作用の暗黙再試行へ結び付けません。
        }
    }
}
