using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

/// <summary>単独実行と常駐プロセスが共有する永続操作状態を管理します。</summary>
public sealed class UpdateJournal
{
    private readonly UpdateLayout layout;
    private readonly string scope;

    /// <summary>検証済みの専用操作パスにだけ保存します。</summary>
    public UpdateJournal(UpdateLayout layout, string peerId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        layout.Validate(layout.JournalPath);
        this.layout = layout;
        scope = "peer:" + peerId + ":updates";
    }

    /// <summary>最後に確定保存した操作を読みます。</summary>
    /// <returns>新規ならnull、既存なら入力と結果です。</returns>
    public async ValueTask<LocalUpdateRecord?> ReadAsync(CancellationToken cancellationToken = default)
    {
        layout.Validate(layout.JournalPath);
        if (!File.Exists(layout.JournalPath))
        {
            return null;
        }
        JsonFileRepository repository = new(layout.JournalPath);
        DurableEntry? entry = await repository.ReadAsync(scope, "operation", cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }
        if (entry.Value is null || entry.Value.Kind != EValueKind.Json || entry.Value.Json.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("更新journalを確認できません。自動初期化しません。");
        }
        return entry.Value.Json.Deserialize<LocalUpdateRecord>(ContractJson.Options) ?? throw new InvalidDataException("更新journalが空です。");
    }

    /// <summary>副作用の前後で入力ハッシュと進行状態を原子的に保存します。</summary>
    /// <returns>永続保存の完了です。</returns>
    public async Task WriteAsync(LocalUpdateJob job, UpdatePeerResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        layout.CreateDirectories();
        layout.Validate(layout.JournalPath);
        JsonFileRepository repository = new(layout.JournalPath);
        LocalUpdateRecord record = new(ContractJson.Hash(job), job, result, ProcessOs.CurrentIdentity());
        EOutcome outcome = await repository.WriteLocalSnapshotAsync(scope, "operation", NodeValue.FromJson(JsonSerializer.SerializeToElement(record, ContractJson.Options)), cancellationToken).ConfigureAwait(false);
        if (outcome != EOutcome.Succeeded)
        {
            throw new IOException("更新状態の耐久保存を確認できません。");
        }
    }

    /// <summary>helper起動を一度だけ予約し、応答喪失でも二重起動しません。</summary>
    /// <returns>この呼出しが起動権を新しく確保したかです。</returns>
    public async ValueTask<bool> ReserveDispatchAsync(LocalUpdateJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        layout.CreateDirectories();
        JsonFileRepository repository = new(layout.JournalPath);
        DurableEntry? existing = await repository.ReadAsync(scope, "dispatch", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return false;
        }
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, "dispatch", null, NodeValue.FromObject(ContractJson.Hash(job)))], cancellationToken).ConfigureAwait(false);
        return receipt.Outcome == EOutcome.Succeeded;
    }
}
