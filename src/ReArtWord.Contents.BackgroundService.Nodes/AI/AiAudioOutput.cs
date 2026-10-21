using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>同期音声APIの一時保存、検証、確定と後始末を共有します。</summary>
internal static class AiAudioOutput
{
    /// <summary>一回だけ生成要求を送り、完全な媒体を確認して公開します。</summary>
    /// <returns>Peer・パス・ハッシュを持つ検証済み生成物です。</returns>
    internal static async Task<NodeResult> GenerateAsync(NodeExecutionContext context, AiRequestSession session, MediaOptions media, Uri endpoint, object body, string mediaType, CancellationToken cancellationToken = default)
    {
        AiMediaOutput.RequireProbe(media);
        NodeSettings settings = new(context.Settings);
        string output = settings.ReadText("outputPath")!;
        string temporary = MediaArtifact.TemporaryPath(media, output, allowExisting: true);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(settings.ReadInt32("downloadTimeoutMs", 300000)));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            _ = await session.DownloadAsync(endpoint, body, temporary, new FileAccessPolicy(media.Files), settings.ReadInt32("maximumBytes", 104857600), true, linked.Token).ConfigureAwait(false);
            session.TerminalKnown = true;
            return await MediaOutputFile.CommitAsync(media, context, temporary, output, mediaType, "audio", cancellationToken: linked.Token).ConfigureAwait(false);
        }
        finally
        {
            MediaArtifact.Cleanup(media, temporary);
        }
    }
}
