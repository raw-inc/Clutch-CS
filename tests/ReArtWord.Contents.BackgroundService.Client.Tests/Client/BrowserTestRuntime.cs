using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ブラウザー保存の所有範囲と障害を再現します。</summary>
internal sealed class BrowserTestRuntime : IJSRuntime
{
    internal Dictionary<Guid, OperationRecord> Operations
    {
        get;
    } = [];

    internal Dictionary<string, string> Local
    {
        get;
    } = new(StringComparer.Ordinal);

    internal Dictionary<string, string> Session
    {
        get;
    } = new(StringComparer.Ordinal);

    internal bool FailWrites
    {
        get; set;
    }

    internal bool RetainDraftOwner
    {
        get; set;
    }

    internal Task? OperationUpdateDelay
    {
        get; set;
    }

    internal Func<string, string, bool>? RejectWrite
    {
        get; set;
    }

    internal Func<string, bool>? RejectRead
    {
        get; set;
    }

    internal string? DocumentLanguage
    {
        get; private set;
    }

    /// <summary>取消指定なしのJS境界です。</summary>
    /// <returns>指定した保存操作の結果です。</returns>
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);

    /// <summary>保存I/Oだけを模倣します。</summary>
    /// <returns>保存文字列またはvoidです。</returns>
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        string operationRead = "contentsOperationStore.read", operationWrite = "contentsOperationStore.write";
        if (string.Equals(identifier, operationRead, StringComparison.Ordinal))
        {
            return ValueTask.FromResult((TValue)(object)JsonSerializer.Serialize(Operations.Values, ContractJson.Options));
        }
        ArgumentNullException.ThrowIfNull(args);
        string claimOwner = "contentsEditorStore.owner";
        if (string.Equals(identifier, claimOwner, StringComparison.Ordinal))
        {
            object claimed = args[1]!;
            if (RetainDraftOwner && args[0] is Guid previous)
            {
                claimed = previous;
            }
            return ValueTask.FromResult((TValue)claimed);
        }
        if (string.Equals(identifier, operationWrite, StringComparison.Ordinal))
        {
            return new(WriteOperationAsync<TValue>((string)args[0]!, (string)args[1]!, cancellationToken));
        }
        string read = "contentsBrowser.read";
        string remove = "contentsEditorStore.removeIfUnchanged";
        string write = "contentsBrowser.write";
        string language = "contentsBrowser.language";
        string key = (string)args[0]!;
        if (string.Equals(identifier, remove, StringComparison.Ordinal))
        {
            bool matches = string.Equals(Local.GetValueOrDefault(key), (string)args[1]!, StringComparison.Ordinal);
            if (matches)
            {
                Local.Remove(key);
            }
            return ValueTask.FromResult((TValue)(object)matches);
        }
        if (string.Equals(identifier, language, StringComparison.Ordinal))
        {
            DocumentLanguage = key;
            return ValueTask.FromResult(default(TValue)!);
        }
        if (string.Equals(identifier, read, StringComparison.Ordinal))
        {
            if (RejectRead?.Invoke(key) == true)
            {
                throw new JSException("storage read unavailable");
            }
            Dictionary<string, string> values = args[1] is true ? Session
                                                                : Local;
            return ValueTask.FromResult((TValue)(object?)values.GetValueOrDefault(key)!);
        }
        if (string.Equals(identifier, write, StringComparison.Ordinal))
        {
            if (FailWrites || RejectWrite?.Invoke(key, (string)args[1]!) == true)
            {
                throw new JSException("quota");
            }
            Dictionary<string, string> values = args[2] is true ? Session
                                                                : Local;
            values[key] = (string)args[1]!;
            return ValueTask.FromResult(default(TValue)!);
        }
        throw new NotSupportedException(identifier);
    }

    /// <summary>操作単位の保存・重複拒否を模倣します。IndexedDBの移行・確定は実ブラウザー試験で確認します。</summary>
    /// <returns>確定した操作のJSONです。</returns>
    private async Task<TValue> WriteOperationAsync<TValue>(string text, string mode, CancellationToken cancellationToken = default)
    {
        string update = "update";
        if (OperationUpdateDelay is { } delay && string.Equals(mode, update, StringComparison.Ordinal))
        {
            await delay.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        string key = "contents.operation-journal";
        if (FailWrites || RejectWrite?.Invoke(key, text) == true)
        {
            throw new JSException("quota");
        }
        OperationRecord item = JsonSerializer.Deserialize<OperationRecord>(text, ContractJson.Options)!;
        if (Operations.TryGetValue(item.OperationId, out OperationRecord? existing))
        {
            string create = "create", track = "track";
            if (string.Equals(mode, create, StringComparison.Ordinal))
            {
                throw new JSException("duplicate operation");
            }
            if (string.Equals(mode, track, StringComparison.Ordinal))
            {
                item = existing;
            }
            else
            {
                item = existing with { Result = item.Result };
            }
        }
        Operations[item.OperationId] = item;
        return (TValue)(object)JsonSerializer.Serialize(item, ContractJson.Options);
    }
}
