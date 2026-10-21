using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>設定の内容版と型付きJSON表現を同じ規則で生成します。</summary>
public static class DeviceSnapshots
{
    /// <summary>取得した値だけを内容版へ結び付けます。</summary>
    /// <returns>独立したJSONと、その内容識別子です。</returns>
    public static DeviceSnapshot Create<T>(T value) => new(ContractJson.Hash(value), JsonSerializer.SerializeToElement(value, ContractJson.Options));

    /// <summary>未知の項目や欠損を許容せず領域の契約へ戻します。</summary>
    /// <returns>取得または入力した型付き値です。</returns>
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(ContractJson.Options) ?? throw new ArgumentException("Device.InvalidData", nameof(value));
}
