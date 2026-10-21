using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

/// <summary>UDP上の有限RPCでPeer間の分割ファイル転送を接続します。</summary>
public interface IPeerFileTransfer
{
    /// <summary>sourceのsizeとdigestを取得します。</summary>
    /// <returns>確認した内容の同一性です。</returns>
    public Task<FileEntryInfo> StatAsync(string peerId, string path, CancellationToken cancellationToken = default);

    /// <summary>元入力で固定した共通版をsource Peerへ伝えます。</summary>
    /// <returns>その方針で確認した内容版です。</returns>
    public Task<FileEntryInfo> StatAsync(string peerId, string path, ClusterOperationalSnapshot? operational, CancellationToken cancellationToken = default)
    {
        if (operational is not null)
        {
            throw new NotSupportedException("共通運用版を伝達するPeer file adapterが必要です。");
        }
        return StatAsync(peerId, path, cancellationToken);
    }

    /// <summary>一階層の一覧を件数ごとに分けて取得します。</summary>
    /// <returns>一覧と継続位置です。</returns>
    public Task<FileListPage> ListAsync(string peerId, string path, string? continuation = null, CancellationToken cancellationToken = default);

    /// <summary>同じ共通版で一覧の各ページを読みます。</summary>
    /// <returns>固定方針で取得した一覧です。</returns>
    public Task<FileListPage> ListAsync(string peerId, string path, string? continuation, ClusterOperationalSnapshot? operational, CancellationToken cancellationToken = default)
    {
        if (operational is not null)
        {
            throw new NotSupportedException("共通運用版を伝達するPeer file adapterが必要です。");
        }
        return ListAsync(peerId, path, continuation, cancellationToken);
    }

    /// <summary>指定内容版の分割データを読みます。</summary>
    /// <returns>32KiB以内の分割データです。</returns>
    public Task<FileChunk> ReadChunkAsync(string peerId, FileChunkRequest request, CancellationToken cancellationToken = default);

    /// <summary>明示要求の同一性を照合して段階を開きます。</summary>
    /// <returns>段階識別子です。</returns>
    public Task<FileStageToken> OpenStageAsync(string peerId, FileStageRequest request, CancellationToken cancellationToken = default);

    /// <summary>段階内のディレクトリを作成します。</summary>
    /// <returns>作成完了です。</returns>
    public Task StageDirectoryAsync(string peerId, FileStageDirectoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>digest検証した分割データだけを書き込みます。</summary>
    /// <returns>書込み完了です。</returns>
    public Task WriteChunkAsync(string peerId, FileChunkWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>全内容とマニフェストを検証して宛先へ確定します。</summary>
    /// <returns>確定した内容です。</returns>
    public Task<FileStageCommitResult> CommitStageAsync(string peerId, FileStageToken token, CancellationToken cancellationToken = default);

    /// <summary>明示不要となった段階を破棄します。</summary>
    /// <returns>破棄完了です。</returns>
    public Task DiscardStageAsync(string peerId, FileStageToken token, CancellationToken cancellationToken = default);
}
