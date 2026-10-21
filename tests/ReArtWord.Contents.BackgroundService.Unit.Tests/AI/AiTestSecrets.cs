using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>外部秘密を読まず固定試験用の構成秘密だけを解決します。</summary>
internal sealed class AiTestSecrets : ISecretStore
{
    /// <summary>試験用秘密を返します。</summary>
    /// <returns>外部サービスでは使用できない固定文字列です。</returns>
    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<string?>("fixture-secret-not-valid");
    }

    /// <summary>この試験ストアへの変更を拒否します。</summary>
    /// <returns>変更非対応の結果です。</returns>
    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException(new NotSupportedException());
    }
}
