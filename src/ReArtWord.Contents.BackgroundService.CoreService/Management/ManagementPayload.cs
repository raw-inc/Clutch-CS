using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>詳細比較を大きな途中バイト配列へ展開せず、通信可能な大きさを確認します。</summary>
internal static class ManagementPayload
{
    /// <summary>一件の詳細が公開応答の余白内に収まるか検証します。</summary>
    /// <returns>容量または変換の省略理由、取得可能ならnullです。</returns>
    public static string? Problem(object value)
    {
        using ManagementResponseBuffer buffer = new();
        try
        {
            JsonSerializer.Serialize(buffer, value, ContractJson.Options);
            if (1048576 < buffer.Length)
            {
                return "DetailTooLarge";
            }
            return null;
        }
        catch (IOException) when (buffer.LimitExceeded)
        {
            return "DetailTooLarge";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return "DetailUnconfirmed";
        }
    }
}
