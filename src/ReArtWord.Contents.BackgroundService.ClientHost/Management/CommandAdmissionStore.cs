using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>クライアント再起動で期限を取り直さないよう、初回の配送期限を元要求へ結び付けます。</summary>
internal sealed class CommandAdmissionStore(string directory)
{
    private readonly Dictionary<Guid, RequestAdmission> pending = [];

    /// <summary>元要求を保存する前に準備状態を作り、通信前の待ち時間も受付期間から消費します。</summary>
    /// <returns>初回だけの準備記録です。呼出元のledger排他内で使用します。</returns>
    internal async Task BeginAsync(ManagementCommand command, CancellationToken cancellationToken = default)
    {
        string fingerprint = ContractJson.Hash(ManagementWire.Request(command, false));
        string path = PathFor(command.RequestId);
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, new CommandAdmissionRecord(fingerprint, false, null), ContractJson.Options, cancellationToken).ConfigureAwait(false);

            // 元要求の保存より先に準備記録を確定し、途中終了後に旧形式として送らないようにします。
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (Guid expired in pending.Where(pair => pair.Value.IsExpired).Select(pair => pair.Key).ToArray())
        {
            pending.Remove(expired);
        }
        pending.Add(command.RequestId, new(TimeProvider.System, command.ExpiresAt - now, command.ExpiresAt, now));
    }

    /// <summary>保存済みの期限は再照会せず復元し、準備中の再起動では新しい受付期間を発行しません。</summary>
    /// <returns>元の配送方式と期限を持つ要求です。</returns>
    // 抑制理由: 保存先はledgerの固定ディレクトリと、解析済みGuidのN形式だけから構成します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003", Justification = "固定保存先内のGuidファイル名を使い、入力文字列をパスへ含めません。")]
    internal async Task<ManagementCommand> PrepareAsync(ManagementCommand command,
        Func<ManagementCommand, CancellationToken, Task<RequestClockStamp?>>? prepare, CancellationToken cancellationToken = default)
    {
        string path = PathFor(command.RequestId);
        if (!File.Exists(path))
        {
            // 既存の要求には新しい時計を割り当てず、元の配送形式で結果を確認します。
            return command with { DeliveryPrepared = true };
        }
        CommandAdmissionRecord record = JsonSerializer.Deserialize<CommandAdmissionRecord>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("操作の受付期限を読み取れません。");
        if (!string.Equals(record.Fingerprint, ContractJson.Hash(ManagementWire.Request(command, false)), StringComparison.Ordinal))
        {
            throw new InvalidDataException("受付期限の記録が操作内容と一致しません。");
        }
        if (!record.Prepared)
        {
            if (prepare is null || !pending.TryGetValue(command.RequestId, out RequestAdmission? admission) || admission.IsExpired)
            {
                throw new InvalidOperationException("操作の受付期限を確認できないため、送信を中止しました。操作記録で結果を確認してから、あらためて実行してください。");
            }
            RequestClockStamp? timing = await prepare(command with { Admission = admission }, cancellationToken).ConfigureAwait(false);
            record = record with { Prepared = true, Timing = timing };
            string temporary = path + ".pending";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, ContractJson.Options), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
            pending.Remove(command.RequestId);
        }
        return command with { DeliveryPrepared = true, DeliveryTiming = record.Timing };
    }

    /// <summary>従来の要求JSONを変更せず、Guidだけから付随記録の場所を求めます。</summary>
    /// <returns>旧版のJSON読取りと混在しないファイルです。</returns>
    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("N") + ".admission");
}
