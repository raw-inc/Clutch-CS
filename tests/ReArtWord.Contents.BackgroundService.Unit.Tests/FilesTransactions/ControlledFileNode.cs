using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>ファイル操作に対応するノードを使用したまま失敗位置を制御します。</summary>
internal sealed class ControlledFileNode(ICompensableFileNode inner) : ICompensableFileNode, IFileFootprintNode
{
    /// <summary>本体の実行回数です。</summary>
    public int Executions
    {
        get; private set;
    }

    /// <summary>全退避後の実処理を制御します。</summary>
    public Func<CancellationToken, Task<NodeResult?>>? BeforeExecution
    {
        get; set;
    }

    /// <summary>退避失敗を再現します。</summary>
    public bool FailCapture
    {
        get; set;
    }

    /// <summary>元のファイル能力契約です。</summary>
    public NodeDescriptor Descriptor => inner.Descriptor;

    /// <summary>元の検証を使用します。</summary>
    /// <returns>設定の問題。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => inner.Validate(definition);

    /// <summary>許可検査済みの実範囲を使用します。</summary>
    /// <returns>読書き範囲。</returns>
    public ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => ((IFileFootprintNode)inner).GetFileAccessAsync(context, cancellationToken);

    /// <summary>設定した境界の後に実ファイルを変更します。</summary>
    /// <returns>元能力の実行結果。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        Executions++;
        if (BeforeExecution is not null)
        {
            NodeResult? result = await BeforeExecution(cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }
        return await inner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>実退避または明示した事前失敗です。</summary>
    /// <returns>実退避トークン。</returns>
    public Task<FileCompensationToken> CaptureAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (FailCapture)
        {
            throw new IOException("capture test failure");
        }
        return inner.CaptureAsync(context, cancellationToken);
    }

    /// <summary>実ファイルを開始前へ戻します。</summary>
    /// <returns>補償完了。</returns>
    public Task RestoreAsync(FileCompensationToken token, CancellationToken cancellationToken = default) => inner.RestoreAsync(token, cancellationToken);

    /// <summary>実退避とローカルリースを解放します。</summary>
    /// <returns>解放完了。</returns>
    public Task ReleaseAsync(FileCompensationToken token, CancellationToken cancellationToken = default) => inner.ReleaseAsync(token, cancellationToken);
}
