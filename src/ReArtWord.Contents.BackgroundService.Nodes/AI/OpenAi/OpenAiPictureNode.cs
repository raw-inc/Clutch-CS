using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;

/// <summary>Images APIのbase64出力を実PNGとして検証します。</summary>
internal sealed class OpenAiPictureNode : AiOperationNode
{
    /// <summary>GPT Image系列用のPicture Generatorを作ります。</summary>
    internal OpenAiPictureNode(ISecretStore secrets, AiOptions options, MediaOptions media) : base(secrets, options, media, "ai.picture.generate", """{"outputPath":{"type":"string","minLength":1},"size":{"type":"string","enum":["auto","1024x1024","1536x1024","1024x1536"]}}""", ["outputPath"])
    {
    }

    /// <summary>一枚の最終画像を取得して実PNGを確定します。</summary>
    /// <returns>Peerと実ファイルの検証情報です。</returns>
    protected override async Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string output = settings.ReadText("outputPath")!;
        string prompt = Prompt(context);
        AiMediaOutput.RequireProbe(Media);
        string temporary = MediaArtifact.TemporaryPath(Media, output, allowExisting: true);
        try
        {
            JsonElement response = await session.JsonAsync("v1/images/generations", new { model = settings.ReadText("model"), prompt, n = 1, output_format = "png", size = settings.ReadText("size", "auto") }, cancellationToken: cancellationToken).ConfigureAwait(false);
            byte[] content = Convert.FromBase64String(response.GetProperty("data")[0].GetProperty("b64_json").GetString()!);
            session.TerminalKnown = true;
            new FileAccessPolicy(Media.Files).ValidatePath(temporary);
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
            return await MediaOutputFile.CommitAsync(Media, context, temporary, output, "image/png", "picture", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MediaArtifact.Cleanup(Media, temporary);
        }
    }
}
