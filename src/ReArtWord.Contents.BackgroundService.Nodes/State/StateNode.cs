using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Nodes.State;

/// <summary>明示された範囲の保存状態と原子的比較更新を利用します。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class StateNode : IActionNode
{
    private const string SettingScope = "scope";
    private const string SettingExpectedVersion = "expectedVersion";
    private const string SettingTtlMilliseconds = "ttlMilliseconds";

    private const string GetOperation = "get";
    private const string DedupeOperation = "dedupe";
    private const string SetOperation = "set";
    private const string DeleteOperation = "delete";
    private readonly INodeStateStore store;
    private readonly string operation;

    /// <summary>状態保存境界と操作を設定します。</summary>
    public StateNode(string operation, INodeStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.operation = operation switch
        {
            "get" or "set" or "compareExchange" or "delete" or "dedupe" => operation,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), "未対応の登録種別です。"),
        };
        this.store = store;
        Descriptor = NodeSupport.Describe($"state.{operation}", $"状態 {operation}", "State",
            """{"type":"object","required":["scope"],"properties":{"scope":{"type":"string","enum":["workflow","peer","cluster"]},"key":{"type":"string","minLength":1},"expectedVersion":{"type":["string","null"]},"ttlMilliseconds":{"type":"number","minimum":1,"maximum":2147483646}}}""",
            [new PortDescriptor("value"), new PortDescriptor("key"), new PortDescriptor("expectedVersion")],
            [new PortDescriptor("value"), new PortDescriptor("version"), new PortDescriptor("applied"), new PortDescriptor("exists"), new PortDescriptor("localOnly"), new PortDescriptor("availability"), new PortDescriptor("persistenceOutcome")],
            ["success", "conflict", "duplicate"]) with { HasAsyncCompletion = true };
        bool updates = operation is "set" or "compareExchange";
        bool compares = operation is "compareExchange";
        bool deduplicates = operation is "dedupe";
        bool available = operation is "get" or "set";
        Descriptor = Descriptor with
        {
            Inputs = [new("value", Required: updates) { IsLegacy = !updates && !deduplicates }, new("key", OverridesSetting: "key"), new("expectedVersion") { IsLegacy = !compares }],
            Outputs = Descriptor.Outputs.Select(port => port with { IsLegacy = !available && port.Name is "availability" or "persistenceOutcome" }).ToArray(),
        };
        System.Text.Json.Nodes.JsonObject schema = System.Text.Json.Nodes.JsonNode.Parse(Descriptor.SettingsSchema.GetRawText())!.AsObject();
        schema["properties"]!["expectedVersion"]!["x-legacy"] = !compares;
        schema["properties"]!["ttlMilliseconds"]!["x-legacy"] = !deduplicates;
        Descriptor = Descriptor with { SettingsSchema = JsonSerializer.SerializeToElement(schema) };
    }

    /// <summary>操作設定と出力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>状態範囲とキー設定を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSupport.Validate(definition, Descriptor);
    }

    /// <summary>比較が不成立の更新を適用済みと扱いません。</summary>
    /// <returns>実際の保存結果、または正常な比較不成立です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        NodeSettings settings = new(context.Settings);
        string? key = settings.ReadText("key");
        string keyPort = "key";
        if (context.Inputs.TryGetValue(keyPort, out NodeValue? suppliedKey))
        {
            key = suppliedKey.GetString();
        }
        if (string.IsNullOrEmpty(key))
        {
            return NodeResult.Failure("state.key.required", "状態キーを文字列で指定してください。", EOutcome.NotExecuted);
        }
        string scope = NodeStateScope.Resolve(settings.ReadText(SettingScope), context.PeerId, context.WorkflowId);
        if (store is IAvailableNodeStateStore available && (string.Equals(operation, GetOperation, StringComparison.Ordinal) || string.Equals(operation, SetOperation, StringComparison.Ordinal)))
        {
            return await ExecuteAvailableAsync(available, context, scope, key, cancellationToken).ConfigureAwait(false);
        }
        VersionedState? current = await store.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (string.Equals(operation, GetOperation, StringComparison.Ordinal))
        {
            return Result(current, false);
        }
        if (string.Equals(operation, DedupeOperation, StringComparison.Ordinal))
        {
            return await DeduplicateAsync(context, scope, key, current, cancellationToken).ConfigureAwait(false);
        }
        NodeValue? value = NodeSupport.Input(context);
        if (string.Equals(operation, DeleteOperation, StringComparison.Ordinal))
        {
            value = null;
        }
        else
        {
            if (value is null)
            {
                return NodeResult.Failure("state.value.required", "更新値がありません。", EOutcome.NotExecuted);
            }
        }
        string? expected = current?.Version;
        string compareOperation = "compareExchange";
        if (string.Equals(operation, compareOperation, StringComparison.Ordinal))
        {
            expected = settings.ReadText("expectedVersion");
            if (context.Inputs.TryGetValue(SettingExpectedVersion, out NodeValue? supplied))
            {
                expected = supplied.GetString();
                if (supplied.Kind != EValueKind.Json || (expected is null && supplied.Json.ValueKind != JsonValueKind.Null))
                {
                    return NodeResult.Failure("state.version.invalid", "期待する版は文字列またはnullです。", EOutcome.NotExecuted);
                }
            }
        }
        StateWriteResult written = await store.CompareExchangeAsync(scope, key, expected, value, cancellationToken).ConfigureAwait(false);
        if (written.Outcome != EOutcome.Succeeded)
        {
            return NodeResult.Failure("state.write.unconfirmed", "状態更新を確認できません。", written.Outcome);
        }
        string route = "conflict";
        if (written.Applied)
        {
            route = "success";
        }
        return Result(written.Current, written.Applied, route, written.LocalOnly);
    }

    /// <summary>get/setだけに保存不能・オフライン方針を適用し、CAS予約へ広げません。</summary>
    /// <returns>採用結果と実保存結果を分けたノード出力です。</returns>
    private async Task<NodeResult> ExecuteAvailableAsync(IAvailableNodeStateStore available, NodeExecutionContext context, string scope, string key, CancellationToken cancellationToken = default)
    {
        AvailabilityValue current = await available.ReadAvailableAsync(scope, key, cancellationToken).ConfigureAwait(false);
        bool applied = false;
        EOutcome persistence = EOutcome.Succeeded;
        NodeResult result = NodeResult.Success();
        string unavailable = "Availability.OfflineUnavailable";
        bool canRead = current.State.CanRead;
        if (!canRead)
        {
            result = NodeResult.Failure(current.State.Reason ?? unavailable, "確認できる内部状態がありません。", EOutcome.NotExecuted);
        }
        if (canRead && string.Equals(operation, SetOperation, StringComparison.Ordinal))
        {
            NodeValue? value = NodeSupport.Input(context);
            if (value is null)
            {
                return NodeResult.Failure("state.value.required", "更新値がありません。", EOutcome.NotExecuted);
            }
            AvailabilityWriteResult written = await available.SetAvailableAsync(scope, key, value, current.Revision, context.ExecutionId, cancellationToken).ConfigureAwait(false);
            current = written.Current ?? current;
            applied = written.Applied;
            persistence = written.PersistenceOutcome;
            if (!applied)
            {
                result = NodeResult.Failure(written.Reason ?? "state.write.unconfirmed", "内部状態の変更を採用できません。", persistence);
                if (persistence == EOutcome.Succeeded)
                {
                    result = new NodeResult(EOutcome.Succeeded, "conflict", new Dictionary<string, NodeValue>(StringComparer.Ordinal));
                }
            }
        }
        return result with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = current.Value?.Copy() ?? NodeValue.Null, ["version"] = NodeValue.FromObject(current.Revision), ["exists"] = NodeValue.FromObject(current.Value is not null), ["applied"] = NodeValue.FromObject(applied), ["localOnly"] = NodeValue.FromObject(current.State.IsLocalUnconfirmed), ["availability"] = NodeValue.FromObject(current.State), ["persistenceOutcome"] = NodeValue.FromObject(persistence) } };
    }

    /// <summary>未存在または明示期限切れのキーを一回だけ確保します。</summary>
    /// <returns>新規確保時だけ元入力を通す結果です。</returns>
    private async ValueTask<NodeResult> DeduplicateAsync(NodeExecutionContext context, string scope, string key, VersionedState? current, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = context.TimeProvider.GetUtcNow();
        if (current is not null && !Expired(current.Value, now))
        {
            return Result(current, false, "duplicate");
        }
        NodeSettings settings = new(context.Settings);
        DateTimeOffset? expiresAt = null;
        if (settings.Get(SettingTtlMilliseconds).ValueKind == JsonValueKind.Number)
        {
            expiresAt = now.AddMilliseconds(settings.ReadNumber("ttlMilliseconds"));
        }
        NodeValue marker = NodeValue.FromObject(new { reservedAt = now, expiresAt });
        StateWriteResult written = await store.CompareExchangeAsync(scope, key, current?.Version, marker, cancellationToken).ConfigureAwait(false);
        if (written.Outcome != EOutcome.Succeeded)
        {
            return NodeResult.Failure("state.dedupe.unconfirmed", "重複抑止の確保を確認できません。", written.Outcome);
        }
        if (!written.Applied)
        {
            return Result(written.Current, false, "duplicate", written.LocalOnly);
        }
        NodeResult result = Result(written.Current, true, localOnly: written.LocalOnly);
        return result with
        {
            Outputs = new Dictionary<string, NodeValue>(result.Outputs, StringComparer.Ordinal)
            {
                ["value"] = NodeSupport.Input(context)?.Copy() ?? NodeValue.Null,
            },
        };
    }

    /// <summary>明示された期限だけを判定します。</summary>
    /// <returns>期限が存在し到達しているかを示します。</returns>
    private static bool Expired(NodeValue value, DateTimeOffset now)
    {
        string expiryField = "expiresAt";
        return value.Kind == EValueKind.Json && value.Json.ValueKind == JsonValueKind.Object && value.Json.TryGetProperty(expiryField, out JsonElement expiry) && expiry.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset time) && time <= now;
    }

    /// <summary>未存在と保存済みnullを別の出力で区別します。</summary>
    /// <returns>実値と版、比較結果です。</returns>
    private static NodeResult Result(VersionedState? state, bool applied, string route = "success", bool localOnly = false)
    {
        return new NodeResult(EOutcome.Succeeded, route, new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = state?.Value.Copy() ?? NodeValue.Null,
            ["version"] = NodeValue.FromObject(state?.Version),
            ["applied"] = NodeValue.FromObject(applied),
            ["exists"] = NodeValue.FromObject(state is not null),
            ["localOnly"] = NodeValue.FromObject(localOnly || state?.LocalOnly == true),
        });
    }
}
