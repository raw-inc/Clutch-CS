using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Nodes.Database.Redis;
using ReArtWord.Contents.BackgroundService.Nodes.Database.Relational;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database;

/// <summary>内部保存と独立した業務データ接続を登録します。</summary>
public static class DatabaseNodeRegistration
{
    /// <summary>接続秘密値とSQLiteの許可パスを共通の所有先から受け取ります。</summary>
    /// <returns>外部データの読取り・書込みノードです。</returns>
    public static IReadOnlyList<INode> Create(ISecretStore secrets, FileSystemOptions files) =>
        [new RelationalNode(false, secrets, files), new RelationalNode(true, secrets, files), new RedisNode(secrets)];
}
