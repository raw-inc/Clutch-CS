using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>Core単位の起動方針を永続化し、他クライアントの変更を上書きしません。</summary>
internal sealed class TerminalSettingsStore(IDurableRepository repository, string peerId)
{
    private string Scope => "peer:" + peerId + ":terminal-settings";

    /// <summary>未設定は従来方式です。保存不能・破損を既定値と取り違えません。</summary>
    /// <returns>Core再起動後も維持される確認済み設定です。</returns>
    internal async Task<TerminalSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, "user-token", cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new();
        }
        TerminalSettings saved = entry.Value?.Json.Deserialize<TerminalSettings>() ?? throw new InvalidDataException("Terminal settings could not be read.");
        return saved with { Version = entry.Version };
    }

    /// <summary>利用者が確認した版にだけ保存し、成功後の値を返します。</summary>
    /// <returns>確認済みの新しい保存版です。</returns>
    internal async Task<TerminalSettings> SaveAsync(TerminalSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DurableMutation mutation = new(Scope, "user-token", settings.Version, NodeValue.FromObject(settings with { Version = null }));
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [mutation], cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidOperationException("Terminal settings were not confirmed. Refresh before changing them again.");
        }
        return settings with { Version = receipt.Entries.Single().Version };
    }
}
