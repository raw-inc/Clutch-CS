namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>既存の受付を変更せず、重複したパスの登録だけを拒否します。</summary>
// 抑制理由: 内部の重複登録だけを表し、競合パスのない生成を許可しません。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "内部登録の競合パスを必須にします。")]
internal sealed class HttpEndpointConflictException : InvalidOperationException
{
    /// <summary>競合したパスを診断へ含めます。</summary>
    internal HttpEndpointConflictException(string path) : base($"Endpoint already registered: {path}")
    {
    }
}
