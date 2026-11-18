using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>ClientHostのHTTP応答に含まれるピアの観測経過時間を、WebUIと同じ形式で読み取ります。</summary>
internal static class ClientObservationJson
{
    /// <summary>保存データやCore間通信と区別した、ピア情報を含むHTTP応答の読取り設定です。</summary>
    internal static JsonSerializerOptions Options
    {
        get;
    } = Create();

    /// <summary>応答の経過時間を、受信後も進むローカルの経過時計へ引き継ぎます。</summary>
    /// <returns>ピア以外の型には共通のJSON契約を適用する設定です。</returns>
    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(ContractJson.Options);
        options.Converters.Add(new PeerObservationJsonConverter(age => new(TimeProvider.System, age)));
        return options;
    }
}
