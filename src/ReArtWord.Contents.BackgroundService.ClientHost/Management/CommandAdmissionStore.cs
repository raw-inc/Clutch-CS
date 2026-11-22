using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>ClientHost再起動後の再送で受付期間が延びないよう、最初に換算した期限を要求IDと内容のハッシュに結び付けて保存します。</summary>
internal sealed class CommandAdmissionStore(string directory, TimeProvider clock)
{
    private readonly Dictionary<Guid, RequestAdmission> pending = [];

    /// <summary>初回の受付期間を引き継ぎ、準備途中の終了を識別できる記録を元の要求より先に保存します。</summary>
    /// <returns>準備中の記録を保存するタスクです。呼出元のClientCommandLedgerで同時書込みを防ぎます。</returns>
    internal async Task BeginAsync(ManagementCommand command, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        RequestAdmission admission = command.Admission ?? new(clock, command.ExpiresAt - now, command.ExpiresAt, now);
        string fingerprint = ContractJson.Hash(ManagementWire.Request(command, false));
        string path = PathFor(command.RequestId);
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, new CommandAdmissionRecord(fingerprint, false, null), ContractJson.Options, cancellationToken).ConfigureAwait(false);

            // この記録がない要求は旧版の形式で再送されます。途中でClientHostが終了しても新しい要求を
            // 旧版の記録と取り違えないよう、元の要求記録より先に「準備中」の状態を書き出します。
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (Guid expired in pending.Where(pair => pair.Value.IsExpired).Select(pair => pair.Key).ToArray())
        {
            pending.Remove(expired);
        }
        pending.Add(command.RequestId, admission);
    }

    /// <summary>保存済みの期限を再利用します。準備中にClientHostが再起動した場合は残り時間を復元できないため、送信を拒否します。</summary>
    /// <returns>初回と同じ通信形式・受付期限で送信できる要求です。</returns>
    // 抑制理由: 保存先はledgerの固定ディレクトリと、解析済みGuidのN形式だけから構成します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003", Justification = "固定保存先内のGuidファイル名を使い、入力文字列をパスへ含めません。")]
    internal async Task<ManagementCommand> PrepareAsync(ManagementCommand command,
        Func<ManagementCommand, CancellationToken, Task<RequestClockStamp?>>? prepare, CancellationToken cancellationToken = default)
    {
        string path = PathFor(command.RequestId);
        if (!File.Exists(path))
        {
            // この追加記録がない要求は旧版が保存したものとして扱います。
            // 期限を新たに換算すると有効期間が変わるため、保存された期限のまま旧形式で再送します。
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

    /// <summary>従来の要求記録とは別の拡張子で、換算した期限の保存先を作ります。</summary>
    /// <returns>要求IDをファイル名にした.admissionファイルのパスです。旧版の*.json読取りには含まれません。</returns>
    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("N") + ".admission");
}
