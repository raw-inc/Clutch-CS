using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;

/// <summary>秘密値の実行時解決と管理向け名前一覧を分離します。</summary>
public sealed class SecretStore : ISecretStore
{
    private readonly IDurableRepository repository;
    private readonly Lock redactionGate = new();
    private readonly HashSet<string> resolvedValues = new(StringComparer.Ordinal);

    /// <summary>用途別の永続保存先を指定します。</summary>
    public SecretStore(IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        this.repository = repository;
    }

    /// <summary>秘密値を共有永続保存する明示境界です。未登録時は変更を拒否します。</summary>
    public Func<Guid, IReadOnlyList<DurableMutation>, CancellationToken, ValueTask<DurableReceipt>>? Replicator
    {
        get; set;
    }

    /// <summary>実行中の接続へ登録済み秘密値を解決します。</summary>
    /// <returns>確認した保存結果です。</returns>
    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("secrets", name, cancellationToken).ConfigureAwait(false);
        string? value = entry?.Value?.GetString();
        if (!string.IsNullOrEmpty(value))
        {
            using (redactionGate.EnterScope())
            {
                resolvedValues.Add(value);
            }
        }
        return value;
    }

    /// <summary>共有永続保存が確認できた場合だけ更新を完了します。</summary>
    /// <returns>確認した保存結果です。</returns>
    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (Replicator is null)
        {
            throw new InvalidOperationException("秘密値の共有保存経路が未設定です。");
        }
        DurableEntry? old = await repository.ReadAsync("secrets", name, cancellationToken).ConfigureAwait(false);
        DurableReceipt result = await Replicator(Guid.NewGuid(), [new DurableMutation("secrets", name, old?.Version, NodeValue.FromObject(value))], cancellationToken).ConfigureAwait(false);
        if (result.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidOperationException("秘密値の共有保存を確認できません。");
        }
    }

    /// <summary>登録名だけを管理操作へ提供します。</summary>
    /// <returns>秘密値を含まない名前一覧。</returns>
    public ValueTask<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        return ListCoreAsync(cancellationToken);
    }

    /// <summary>保存済み値を投影して名前だけに限定します。</summary>
    /// <returns>名前一覧。</returns>
    private async ValueTask<IReadOnlyList<string>> ListCoreAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("secrets", cancellationToken).ConfigureAwait(false);
        return entries.Where(entry => entry.Value is not null).Select(entry => entry.Key).ToArray();
    }

    /// <summary>現在値と、この実体で開始済み処理へ渡した旧値をログ保護に使用します。</summary>
    /// <returns>管理APIへ公開しない伏字判定用の値です。</returns>
    internal async ValueTask<IReadOnlyList<string>> ReadRedactionValuesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("secrets", cancellationToken).ConfigureAwait(false);
        using (redactionGate.EnterScope())
        {
            return entries.Select(entry => entry.Value?.GetString()).Where(value => !string.IsNullOrEmpty(value)).Select(value => value!)
                .Concat(resolvedValues).Distinct(StringComparer.Ordinal).ToArray();
        }
    }
}
