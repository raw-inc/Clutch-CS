using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>不完全パケットを上限・期限付きで組み立てます。</summary>
internal sealed class FragmentAssembler
{
    private readonly Dictionary<string, FragmentAssembly> assemblies = new(StringComparer.Ordinal);
    private readonly int maximumBytes;
    private readonly TimeProvider clock;
    private readonly DatagramRecipient? recipient;
    private readonly HashSet<string> ignored = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset ExpiresAt)> ignoredOrder = [];
    private readonly List<string> expiredKeys = new(32);
    private DateTimeOffset nextPrune;

    /// <summary>一配送上限を指定します。</summary>
    public FragmentAssembler(int maximumBytes, TimeProvider clock, DatagramRecipient? recipient = null)
    {
        this.maximumBytes = maximumBytes;
        this.clock = clock;
        this.recipient = recipient;
    }

    /// <summary>旧形式の宛先を先頭の小さい範囲だけで検査し、対象外のBase64本文を生成しません。</summary>
    /// <returns>従来の厳密な契約検証を通した断片、または受信対象外のnullです。</returns>
    internal DatagramFragment? Decode(string sender, ReadOnlySpan<byte> packet)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Prune(now);
        Utf8JsonReader reader = new(packet);
        Guid id = Guid.Empty;
        int index = -1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                continue;
            }
            bool identifier = reader.ValueTextEquals(nameof(DatagramFragment.Id));
            bool position = reader.ValueTextEquals(nameof(DatagramFragment.Index));
            bool payload = reader.ValueTextEquals(nameof(DatagramFragment.Payload));
            if (!reader.Read())
            {
                break;
            }
            if (identifier && reader.TokenType == JsonTokenType.String)
            {
                _ = reader.TryGetGuid(out id);
            }
            if (position && reader.TokenType == JsonTokenType.Number)
            {
                _ = reader.TryGetInt32(out index);
            }
            if (payload)
            {
                string key = sender + id.ToString("N");
                if (id != Guid.Empty && ignored.Contains(key))
                {
                    return null;
                }
                if (id != Guid.Empty && index == 0 && reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped)
                {
                    Span<byte> prefix = stackalloc byte[512];
                    ReadOnlySpan<byte> encoded = reader.ValueSpan[..Math.Min(reader.ValueSpan.Length, 680)];
                    _ = Base64.DecodeFromUtf8(encoded, prefix, out _, out int written, isFinalBlock: false);
                    if (recipient?.Excludes(prefix[..written]) == true)
                    {
                        Ignore(key, now);
                        return null;
                    }
                }
                break;
            }
        }
        return JsonSerializer.Deserialize<DatagramFragment>(packet, ContractJson.Options);
    }

    /// <summary>除外済みと未完成の追跡を、通信内容にかかわらず同じ期限で回収します。</summary>
    private void Prune(DateTimeOffset now)
    {
        if (nextPrune <= now)
        {
            foreach (KeyValuePair<string, FragmentAssembly> pair in assemblies)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    expiredKeys.Add(pair.Key);
                }
            }
            foreach (string expired in expiredKeys)
            {
                assemblies.Remove(expired);
            }
            expiredKeys.Clear();
            while (ignoredOrder.TryPeek(out (string Key, DateTimeOffset ExpiresAt) oldest) && oldest.ExpiresAt <= now)
            {
                ignored.Remove(oldest.Key);
                ignoredOrder.Dequeue();
            }
            nextPrune = now.AddSeconds(1);
        }
    }

    /// <summary>対象外配送を有限件数だけ保持し、後続断片も本文を復号せず捨てます。</summary>
    private void Ignore(string key, DateTimeOffset now)
    {
        assemblies.Remove(key);
        if (1024 <= ignoredOrder.Count)
        {
            ignored.Remove(ignoredOrder.Dequeue().Key);
        }
        if (ignored.Add(key))
        {
            ignoredOrder.Enqueue((key, now.AddSeconds(15)));
        }
    }

    /// <summary>分割内容を検査し、揃った配送だけを返します。</summary>
    /// <returns>完全本文、または不足・不正時null。</returns>
    public byte[]? Add(string sender, DatagramFragment fragment)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Prune(now);
        if (fragment.Count <= 0 || 512 < fragment.Count || fragment.Index < 0 || fragment.Count <= fragment.Index || fragment.Length <= 0 || maximumBytes < fragment.Length || 12000 < fragment.Payload.Length)
        {
            return null;
        }
        string key = sender + fragment.Id.ToString("N");
        if (ignored.Contains(key))
        {
            return null;
        }
        if (fragment.Index == 0 && recipient?.Excludes(fragment.Payload) == true)
        {
            Ignore(key, now);
            return null;
        }
        if (fragment.Count == 1)
        {
            if (fragment.Payload.Length == fragment.Length && string.Equals(Convert.ToHexString(SHA256.HashData(fragment.Payload)), fragment.Hash, StringComparison.Ordinal))
            {
                return fragment.Payload;
            }
            return null;
        }
        bool transient = fragment.Index == 0 && IsTransient(fragment.Payload);
        if (!assemblies.TryGetValue(key, out FragmentAssembly? assembly))
        {
            if (!MakeRoom(fragment.Index, transient))
            {
                return null;
            }
            assembly = new FragmentAssembly(fragment, now.AddSeconds(15));
            assemblies.Add(key, assembly);
        }
        if (assembly.First.Count != fragment.Count || assembly.First.Length != fragment.Length || !string.Equals(assembly.First.Hash, fragment.Hash, StringComparison.Ordinal))
        {
            assemblies.Remove(key);
            return null;
        }
        if (fragment.Index == 0)
        {
            assembly.Transient = transient;
        }
        assembly.Parts.TryAdd(fragment.Index, fragment.Payload);
        if (assembly.Parts.Count != fragment.Count)
        {
            return null;
        }
        assemblies.Remove(key);
        if (assembly.Parts.Values.Sum(part => part.Length) != fragment.Length)
        {
            return null;
        }
        byte[] body = new byte[fragment.Length];
        int position = 0;
        for (int index = 0; index < fragment.Count; index++)
        {
            byte[] part = assembly.Parts[index];
            part.CopyTo(body, position);
            position += part.Length;
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(body)), fragment.Hash, StringComparison.Ordinal))
        {
            return null;
        }
        return body;
    }

    /// <summary>先頭のない断片と未完了通知を入れ替え、ACKが必要な配送の枠を確保します。</summary>
    /// <returns>32配送の上限内で開始できる場合だけtrueです。</returns>
    private bool MakeRoom(int index, bool transient)
    {
        if (assemblies.Count < 32)
        {
            return true;
        }
        if (index != 0)
        {
            return false;
        }
        string? oldestKey = null;
        DateTimeOffset oldestExpiry = DateTimeOffset.MaxValue;
        foreach (KeyValuePair<string, FragmentAssembly> entry in assemblies)
        {
            bool replaceable = !entry.Value.Parts.ContainsKey(0) || (!transient && entry.Value.Transient);
            if (replaceable && (oldestKey is null || entry.Value.ExpiresAt < oldestExpiry))
            {
                oldestKey = entry.Key;
                oldestExpiry = entry.Value.ExpiresAt;
            }
        }
        return oldestKey is not null && assemblies.Remove(oldestKey);
    }

    /// <summary>本文より前の種類だけを読み、不明な配送は保存要求と同じように保護します。</summary>
    /// <returns>先頭で一時通知と確認できた場合だけtrueです。</returns>
    private static bool IsTransient(ReadOnlySpan<byte> prefix)
    {
        string bodyName = "body";
        string kindName = "kind";
        Utf8JsonReader reader = new(prefix, isFinalBlock: false, state: default);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }
                if (reader.ValueTextEquals(nameof(WireMessage.Body)) || reader.ValueTextEquals(bodyName))
                {
                    break;
                }
                if (reader.ValueTextEquals(nameof(WireMessage.Kind)) || reader.ValueTextEquals(kindName))
                {
                    return reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("transient");
                }
            }
        }
        catch (JsonException)
        {
            // 種類が先頭に収まらない旧形式も、完成するまで保護します。
        }
        return false;
    }
}
