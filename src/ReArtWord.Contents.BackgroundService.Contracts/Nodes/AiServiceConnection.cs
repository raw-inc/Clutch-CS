namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>対応AIサービスの接続先を、実行処理と設定画面で共有します。</summary>
public static class AiServiceConnection
{
    /// <summary>ノードとリージョンに対応する公式接続先を取得します。</summary>
    /// <returns>AI通信を行わないノードや未対応リージョンはnullです。</returns>
    public static Uri? Endpoint(string nodeType, string region = "singapore", string? workspaceId = null) => nodeType switch
    {
        "ai.text.generate" or "ai.audio.generate" or "ai.picture.generate" or "ai.content.route" or "ai.openai.chat" or "ai.openai.responses" => new("https://api.openai.com/"),
        "ai.audio.elevenlabs.generate" or "ai.audio.elevenlabs.v3.generate" or "ai.audio.elevenlabs.v4.generate" or "ai.audio.elevenlabs.soundEffects" => new("https://api.elevenlabs.io/"),
        "ai.audio.qwen.audio3.generate" => WorkspaceEndpoint(region, workspaceId),
        "ai.audio.qwen.generate" or "ai.audio.qwen.tts.generate" or "ai.video.generate" => region switch
        {
            "singapore" => new("https://dashscope-intl.aliyuncs.com/"),
            "beijing" => new("https://dashscope.aliyuncs.com/"),
            _ => null,
        },
        _ => null,
    };

    /// <summary>Workspace IDを一つのDNSラベルとして検証し、公式ドメインへ限定します。</summary>
    /// <returns>指定リージョンの基点、または未設定・不正な場合はnullです。</returns>
    private static Uri? WorkspaceEndpoint(string region, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || 63 < workspaceId.Length || workspaceId.StartsWith('-') || workspaceId.EndsWith('-')
            || workspaceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            return null;
        }
        string? location = region switch { "singapore" => "ap-southeast-1", "beijing" => "cn-beijing", _ => null };
        return location is null ? null
                                : new("https://" + workspaceId + "." + location + ".maas.aliyuncs.com/");
    }
}
