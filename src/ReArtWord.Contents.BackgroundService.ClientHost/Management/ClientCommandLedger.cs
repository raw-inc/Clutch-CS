using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>送信前に元の操作実体と指紋を記録し、再起動後も対象を差し替えません。</summary>
internal sealed class ClientCommandLedger : IDisposable
{
    private readonly string directory;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly CommandAdmissionStore admissions;

    /// <summary>要求本文を保存しない専用ディレクトリを作成します。</summary>
    public ClientCommandLedger(string stateDirectory)
    {
        directory = Path.Combine(stateDirectory, "commands");
        Directory.CreateDirectory(directory);
        admissions = new(directory);
    }

    /// <summary>一つのドメイン操作IDを各管理段階の外側IDへ一意に対応させます。</summary>
    /// <returns>元IDと操作名で固定されたIDです。</returns>
    public static Guid Identity(Guid id, string operation) => ManagementCommandIdentity.Create(id, operation);

    /// <summary>開始する実体を固定してから通信へ渡します。</summary>
    /// <returns>初回の実体と期限を維持する要求です。</returns>
    public Task<ManagementCommand> PrepareAsync(Guid id, string clusterId, string peerId, string operation, NodeValue? payload, PeerInfo? peer, CancellationToken cancellationToken = default)
        => PrepareCoreAsync(id, clusterId, peerId, operation, payload, peer, null, cancellationToken);

    /// <summary>送信先と要求内容のハッシュを記録してからCoreの受付時計を照会し、換算した期限も保存してから送信に渡します。</summary>
    /// <returns>ClientHost再起動後も、初回と同じ送信先・本文・受付期限を使う要求です。</returns>
    public Task<ManagementCommand> PrepareTimedAsync(Guid id, string clusterId, string peerId, string operation, NodeValue? payload, PeerInfo? peer,
        Func<ManagementCommand, CancellationToken, Task<Contracts.Execution.RequestClockStamp?>> prepare, CancellationToken cancellationToken = default)
        => PrepareCoreAsync(id, clusterId, peerId, operation, payload, peer, prepare, cancellationToken);

    /// <summary>初回の送信先Coreの実体ID、要求本文のハッシュ、受付期限を記録します。</summary>
    /// <returns>初回に確定した情報から構成した要求です。同じIDで対象や内容が変わった場合は例外にします。</returns>
    // 抑制理由: ファイル名は解析済みGuidのN形式と固定拡張子のみで構成し、入力文字列をパスへ連結しません。
    [SuppressMessage("Security", "CA3003", Justification = "信頼したstate directory配下でGuidの32桁16進表記だけをfile名に使用します。")]
    private async Task<ManagementCommand> PrepareCoreAsync(Guid id, string clusterId, string peerId, string operation, NodeValue? payload, PeerInfo? peer,
        Func<ManagementCommand, CancellationToken, Task<Contracts.Execution.RequestClockStamp?>>? prepare, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClientCommandRecord? previous = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
            string hash = ContractJson.Hash(payload);
            if (previous is not null)
            {
                if (!string.Equals(previous.ClusterId, clusterId, StringComparison.Ordinal) || !string.Equals(previous.TargetPeerId, peerId, StringComparison.Ordinal)
                    || !string.Equals(previous.Operation, operation, StringComparison.Ordinal) || !string.Equals(previous.PayloadHash, hash, StringComparison.Ordinal))
                {
                    throw new ArgumentException("同じ要求IDの対象や内容が一致しません。", nameof(id));
                }
                ManagementCommand restored = new(previous.RequestId, previous.ClusterId, previous.TargetPeerId, previous.TargetInstanceId, previous.ExpiresAt, previous.Operation, payload);
                return await admissions.PrepareAsync(restored, prepare, cancellationToken).ConfigureAwait(false);
            }
            if (peer is null)
            {
                throw new InvalidOperationException("元の実行先を発見できないため送信しません。");
            }
            ManagementCommand command = new(id, clusterId, peerId, peer.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5), operation, payload);
            ClientCommandRecord record = new(id, clusterId, peerId, peer.InstanceId, command.ExpiresAt, operation, hash, ContractJson.Hash(ManagementWire.Request(command, false)));
            if (prepare is not null)
            {
                await admissions.BeginAsync(command, cancellationToken).ConfigureAwait(false);
            }
            string path = Path.Combine(directory, id.ToString("N") + ".json");
            string pending = path + ".pending";
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(record, ContractJson.Options), cancellationToken).ConfigureAwait(false);
            File.Move(pending, path, false);
            if (prepare is not null)
            {
                return await admissions.PrepareAsync(command, prepare, cancellationToken).ConfigureAwait(false);
            }
            return command;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>再実行せずに元要求の照会情報を取得します。</summary>
    /// <returns>送信前に保存された固定情報です。</returns>
    // 抑制理由: 外部入力はGuid型に変換済みで、N形式からディレクトリ traversalを構成できません。
    [SuppressMessage("Security", "CA3003", Justification = "固定directory内のGuidだけで作ったfile名を参照します。")]
    public async Task<ClientCommandRecord?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(directory, id.ToString("N") + ".json");
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize<ClientCommandRecord>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("元の要求記録を読めません。");
    }

    /// <summary>所有する書込排他を解放します。</summary>
    public void Dispose() => changes.Dispose();
}
