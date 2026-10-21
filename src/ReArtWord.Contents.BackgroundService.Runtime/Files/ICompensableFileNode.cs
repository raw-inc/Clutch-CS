using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files;

/// <summary>複数Peerの実行前退避と明示補償を接続するファイルノードです。</summary>
public interface ICompensableFileNode : IActionNode
{
    /// <summary>実行前状態を退避して対象範囲の競合リースを保持します。</summary>
    /// <returns>退避状態の識別子です。</returns>
    public Task<FileCompensationToken> CaptureAsync(NodeExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>明示された全対象失敗時の補償を行います。</summary>
    /// <returns>復元完了です。</returns>
    public Task RestoreAsync(FileCompensationToken token, CancellationToken cancellationToken = default);

    /// <summary>退避と競合リースを解放します。復元は行いません。</summary>
    /// <returns>解放完了です。</returns>
    public Task ReleaseAsync(FileCompensationToken token, CancellationToken cancellationToken = default);
}
