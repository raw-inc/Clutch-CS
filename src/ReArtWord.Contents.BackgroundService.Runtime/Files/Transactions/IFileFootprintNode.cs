using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;

/// <summary>実ファイルを読む・変更するノードの予約範囲を取得します。</summary>
public interface IFileFootprintNode : IActionNode
{
    /// <summary>実行地点で許可検査したローカルパスと、別Peerが検証する読取り元を返します。</summary>
    /// <returns>実ファイルアクセス範囲です。</returns>
    public ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessAsync(NodeExecutionContext context, CancellationToken cancellationToken = default);
}
