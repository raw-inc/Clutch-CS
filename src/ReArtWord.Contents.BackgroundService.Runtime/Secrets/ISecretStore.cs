namespace ReArtWord.Contents.BackgroundService.Runtime.Secrets;

/// <summary>接続時だけ秘密値を解決する境界です。クライアントへ値を返しません。</summary>
public interface ISecretStore
{
    /// <summary>指定した秘密値を実行中の接続へ提供します。</summary>
    /// <returns>登録された値。未登録ならnullです。</returns>
    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>秘密値を登録・更新します。</summary>
    /// <returns>保存の完了です。</returns>
    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default);
}
