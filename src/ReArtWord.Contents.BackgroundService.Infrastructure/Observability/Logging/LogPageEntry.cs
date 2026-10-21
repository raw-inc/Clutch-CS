using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>元ログの位置を保持し、巨大な詳細だけを転送から除きます。</summary>
internal readonly record struct LogPageEntry(WorkflowLogEntry Entry, LogEntryOmission? Omission)
{
    private static JsonSerializerOptions SerializationOptions
    {
        get;
    } = new(ContractJson.Options) { MaxDepth = 40 };

    /// <summary>一件の大きさを確認し、ページが進まなくなる巨大項目を要約します。</summary>
    /// <returns>表示用ログと省略範囲です。</returns>
    internal static LogPageEntry Create(WorkflowLogEntry entry, int maximumBytes)
    {
        string reason = "log.details.size";
        try
        {
            if (JsonSerializer.SerializeToUtf8Bytes(entry, SerializationOptions).Length <= maximumBytes)
            {
                return new(entry, null);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            reason = "log.details.serialization";
        }
        List<string> fields = [];
        if (entry.Data is not null)
        {
            fields.Add("data");
        }
        if (entry.Raw is not null)
        {
            fields.Add("raw");
        }
        WorkflowLogEntry bounded = entry with
        {
            Data = null, Raw = null, Message = Short(entry.Message, "message", fields)!,
            Level = Short(entry.Level, "level", fields)!, PeerId = Short(entry.PeerId, "peerId", fields)!,
            WorkflowId = Short(entry.WorkflowId, "workflowId", fields), NodeId = Short(entry.NodeId, "nodeId", fields),
        };
        return new(bounded, new(entry.Id, fields, reason));
    }

    /// <summary>短縮した項目を明示して文字列を限定します。</summary>
    /// <returns>短い文字列または元のnullです。</returns>
    private static string? Short(string? value, string field, List<string> fields)
    {
        if (value?.Length is > 128)
        {
            fields.Add(field);
            return value[..128] + "…";
        }
        return value;
    }
}
