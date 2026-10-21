using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Files;

/// <summary>固定対象への順序付き通信を所有し、未対応先には従来の分割単位を使います。</summary>
public sealed class FileTransferClient : IDisposable
{
    private readonly PeerManagementClient remote;
    private readonly FileTransferPeer target;
    private readonly TcpClient? client;
    private readonly NetworkBindingLease? binding;
    private bool failed;

    /// <summary>開始時だけ選択した通信を所有し、送信後には別経路へ切り替えません。</summary>
    internal FileTransferClient(PeerManagementClient remote, FileTransferPeer target, TcpClient? client = null, NetworkBindingLease? binding = null)
    {
        this.remote = remote;
        this.target = target;
        this.client = client;
        this.binding = binding;
    }

    /// <summary>今回の相手と通信で使用できる分割サイズです。</summary>
    public int ChunkBytes => client is null ? FileTransferLimits.DatagramBytes
                                            : FileTransferLimits.StreamBytes;

    /// <summary>同じサービスの固定内容版を取得します。</summary>
    /// <returns>ファイルの内容版です。</returns>
    public Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync<FileEntryInfo>("transfer.stat", path, true, cancellationToken);

    /// <summary>同じファイル許可で有限の一覧を読みます。</summary>
    /// <returns>内容版を含む一覧です。</returns>
    public Task<FileListPage> ListAsync(FileTransferListRequest request, CancellationToken cancellationToken = default)
        => SendAsync<FileListPage>("transfer.list", request, true, cancellationToken);

    /// <summary>位置、長さ、分割digestを検証し、壊れたデータを中継しません。</summary>
    /// <returns>確認済みの分割データです。</returns>
    public async Task<FileChunk> ReadAsync(FileChunkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileChunk chunk = await SendAsync<FileChunk>("transfer.read", request, true, cancellationToken).ConfigureAwait(false);
        if (chunk.Offset != request.Offset || chunk.Data.Length != Math.Min(request.Count, request.ExpectedLength - request.Offset)
            || !string.Equals(chunk.Sha256, Convert.ToHexString(SHA256.HashData(chunk.Data.Span)), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The source chunk could not be verified.");
        }
        return chunk;
    }

    /// <summary>同じ段階へ書き込み、永続保存の確認を受けます。</summary>
    /// <returns>分割データの保存完了です。</returns>
    public async Task WriteAsync(FileChunkWriteRequest request, CancellationToken cancellationToken = default)
    {
        _ = await SendAsync<bool>("transfer.write", request, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一要求の期限を通信と処理へ共通に渡し、失敗した接続を再送しません。</summary>
    /// <returns>一度だけ送った要求の確認結果です。</returns>
    private async Task<T> SendAsync<T>(string operation, object payload, bool readOnly, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(failed, this);
        if (client is null)
        {
            return await remote.SendAsync<T>(target, operation, payload, readOnly, cancellationToken).ConfigureAwait(false);
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, binding!.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ManagementCommand command = new(Guid.NewGuid(), target.ClusterId, target.PeerId, target.InstanceId, DateTimeOffset.UtcNow.AddMinutes(2), operation,
            NodeValue.FromJson(JsonSerializer.SerializeToElement(payload, ContractJson.Options)));
        bool received = false;
        try
        {
            await FileTransferWire.WriteAsync(client.GetStream(), command, timeout.Token).ConfigureAwait(false);
            ManagementReply reply = await FileTransferWire.ReadAsync<ManagementReply>(client.GetStream(), timeout.Token).ConfigureAwait(false);
            received = true;
            if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
            {
                throw new IOException($"The peer did not confirm {operation} ({reply.Outcome}: {reply.Reason}).");
            }
            return reply.Value.Json.Deserialize<T>(ContractJson.Options) ?? throw new InvalidDataException("Invalid file transfer response.");
        }
        finally
        {
            if (!received)
            {
                failed = true;
            }
        }
    }

    /// <summary>所有する接続と固定NICの監視を解放します。</summary>
    public void Dispose()
    {
        failed = true;
        client?.Dispose();
        binding?.Dispose();
    }
}
