using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>通信試験でのみ使用する秘密値保管です。</summary>
internal sealed class TestSecretStore : ISecretStore
{
    private string? value;

    /// <summary>試験で使用する秘密値を固定します。</summary>
    internal TestSecretStore(string? value = null)
    {
        this.value = value;
    }

    /// <summary>登録した試験値を返します。</summary>
    /// <returns>登録値または未登録のnullです。</returns>
    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(value);
    }

    /// <summary>試験値を更新します。</summary>
    /// <returns>更新完了です。</returns>
    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        this.value = value;
        return ValueTask.CompletedTask;
    }
}
