using ReArtWord.Contents.BackgroundService.Nodes.AI.ElevenLabs;
using ReArtWord.Contents.BackgroundService.Nodes.AI.OpenAi;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>検証可能な外部サービスのAPI接続処理を列挙して登録します。</summary>
public static class AiNodeRegistration
{
    /// <summary>秘密ストアと接続・ファイル方針から具体ノードを作ります。</summary>
    /// <returns>四媒体Generatorとrouterです。</returns>
    public static IReadOnlyList<INode> Create(ISecretStore secrets, AiOptions options, MediaOptions mediaOptions)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mediaOptions);
        return [new OpenAiGenerationNode(secrets, options, mediaOptions, responses: false), new OpenAiGenerationNode(secrets, options, mediaOptions, responses: true), new OpenAiMessageNode(responses: false), new OpenAiMessageNode(responses: true), new OpenAiTextNode(secrets, options, mediaOptions), new SpeechNode(secrets, options, mediaOptions), new SpeechNode(secrets, options, mediaOptions, qwen: true), new SpeechNode(secrets, options, mediaOptions, qwen: true, clone: false), new ElevenLabsSpeechNode(secrets, options, mediaOptions), new ElevenLabsSpeechNode(secrets, options, mediaOptions, v3: true), new ElevenLabsDialogueNode(secrets, options, mediaOptions), new QwenAudioSpeechNode(secrets, options, mediaOptions), new ElevenLabsSoundEffectsNode(secrets, options, mediaOptions), new OpenAiPictureNode(secrets, options, mediaOptions), new WanVideoNode(secrets, options, mediaOptions), new AiDestinationRouterNode(), new AiContentRouterNode(secrets, options, mediaOptions)];
    }
}
