using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>先頭の断片で確認できる宛先を調べ、無関係な通信の組立てを避けます。</summary>
internal sealed class DatagramRecipient(string receiverId, Guid instanceId, string? clusterId)
{
    /// <summary>本文を読む前に、自分が受け取らない配送と確定できるものだけを除外します。</summary>
    /// <returns>別宛先・別クラスタ・自分の送信であることを確認できた場合はtrueです。</returns>
    internal bool Excludes(ReadOnlySpan<byte> prefix)
    {
        Utf8JsonReader reader = new(prefix, isFinalBlock: false, state: default);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }
                string? property = reader.GetString();
                string body = nameof(WireMessage.Body);
                if (string.Equals(property, body, StringComparison.OrdinalIgnoreCase) || !reader.Read())
                {
                    break;
                }
                if (reader.TokenType == JsonTokenType.String && ExcludesProperty(property, ref reader))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // 先頭に宛先が収まらない旧形式も、完全な本文の検証で判定します。
        }
        return false;
    }

    /// <summary>本文より先に届いた配送先と送信実体を照合します。</summary>
    /// <returns>この受信者に関係しない項目を確認できた場合はtrueです。</returns>
    private bool ExcludesProperty(string? property, ref Utf8JsonReader reader)
    {
        string cluster = nameof(WireMessage.ClusterId);
        string target = nameof(WireMessage.TargetId);
        string sourceInstance = nameof(WireMessage.InstanceId);
        string targetInstance = nameof(WireMessage.TargetInstanceId);
        string kind = nameof(WireMessage.Kind);
        if (string.Equals(property, kind, StringComparison.OrdinalIgnoreCase))
        {
            return clusterId is not null && reader.ValueTextEquals(DiscoveryProtocol.AnnouncementKind);
        }
        if (string.Equals(property, cluster, StringComparison.OrdinalIgnoreCase))
        {
            return clusterId is not null && !reader.ValueTextEquals(clusterId);
        }
        if (string.Equals(property, target, StringComparison.OrdinalIgnoreCase))
        {
            return !reader.ValueTextEquals(receiverId);
        }
        if (string.Equals(property, sourceInstance, StringComparison.OrdinalIgnoreCase))
        {
            return reader.TryGetGuid(out Guid source) && source == instanceId;
        }
        return string.Equals(property, targetInstance, StringComparison.OrdinalIgnoreCase)
            && reader.TryGetGuid(out Guid destination) && destination != instanceId;
    }
}
