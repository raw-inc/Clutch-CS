using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>更新途中も旧ClientHostが接続ticketを厳密に読めることを確認します。</summary>
public sealed class DesktopCompatibilityTests
{
    /// <summary>追加能力は別照会に置き、旧版の未知property拒否規則と互換性を維持します。</summary>
    [Fact]
    public void CurrentTicketCanBeReadByStrictLegacyClient()
    {
        DesktopTicket current = new(50001, new string('A', 64), DateTimeOffset.UtcNow.AddSeconds(15), true);
        string json = JsonSerializer.Serialize(current, ContractJson.Options);
        LegacyTicket legacy = JsonSerializer.Deserialize<LegacyTicket>(json, ContractJson.Options)!;
        Assert.Equal(new LegacyTicket(current.Port, current.Token, current.ExpiresAt, true), legacy);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LegacyTicket>(json[..^1] + ",\"viewOnly\":true}", ContractJson.Options));
    }

    /// <summary>c22dc68を含む配布済みClientHostの接続ticket契約です。</summary>
    private sealed record LegacyTicket(int Port, string Token, DateTimeOffset ExpiresAt, bool CodecDetails = false);
}
